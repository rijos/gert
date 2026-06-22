using System.Threading.Tasks.Dataflow;
using Gert.Service.Chat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gert.Agent;

/// <summary>
/// The production <see cref="ITurnQueue"/> - launches a planned turn on a TPL Dataflow
/// <see cref="ActionBlock{T}"/> and bounds how many run at once with the block's
/// <see cref="ExecutionDataflowBlockOptions.MaxDegreeOfParallelism"/> (replacing the old sharded
/// queue + worker, then the hand-rolled <c>SemaphoreSlim</c> + inflight roster). Per-conversation
/// serialization is NOT this type's job: the <c>ux_messages_streaming</c> gate index already admits
/// at most one live turn per conversation (a second is 409'd at plan time), so a global concurrency
/// cap is all that is left (chat-and-tools.md section detached turns, decisions.md#11).
///
/// <para>
/// <see cref="EnqueueAsync"/> returns immediately (POST never blocks): it posts to the unbounded
/// block, which runs the turn off-thread when a slot frees - opening a fresh DI scope, seeding its
/// <see cref="DetachedUserContext"/> from the job FIRST (so the scoped tools resolve with the
/// plan-time identity + entitlement snapshot), then running the turn through <see cref="ITurnRunner"/>.
/// A job that waits for a slot still ages from <c>PlannedAt</c> (the runner caps its lifetime at the
/// remaining budget), so queue wait counts against the turn exactly as before. In-memory and
/// non-durable by design - the orphan rule covers a turn lost to a crash. As an
/// <see cref="IHostedService"/>, <see cref="StopAsync"/> cancels in-flight runners (they
/// error-finalise) and best-effort drains them within the shutdown window.
/// </para>
/// </summary>
public sealed class TurnLauncher : ITurnQueue, IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TurnLauncher> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ActionBlock<TurnJob> _runner;

    public TurnLauncher(
        IServiceScopeFactory scopeFactory,
        IOptions<TurnOptions> options,
        ILogger<TurnLauncher> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // The global concurrency cap IS MaxDegreeOfParallelism: the block runs at most this many
        // turns at once and queues the rest, unbounded so EnqueueAsync (the POST path) never blocks.
        // Host options validation enforces >= 1; the Max is belt-and-braces for direct construction.
        _runner = new ActionBlock<TurnJob>(
            RunAsync,
            new ExecutionDataflowBlockOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, options.Value.MaxConcurrentTurns),
                BoundedCapacity = DataflowBlockOptions.Unbounded,
            });
    }

    /// <inheritdoc />
    public Task EnqueueAsync(TurnJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        // Fire-and-forget: POST returns at once; the block runs the turn off-thread when a slot frees.
        // Post only fails once StopAsync has completed the block - the turn is then dropped to the
        // orphan rule, which ages its streaming placeholder to error.
        if (!_runner.Post(job))
        {
            _logger.LogWarning(
                "Turn for conversation {ConversationId} in project {Pid} was not queued (launcher stopping); the orphan rule is the backstop.",
                job.ConversationId, job.Pid);
        }

        return Task.CompletedTask;
    }

    private async Task RunAsync(TurnJob job)
    {
        // A job still queued behind the cap when shutdown began: drop it untouched - the turn never
        // started, so the orphan rule ages its streaming row. No graceful drain of the queue by design.
        if (_stopping.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();

            // Seed identity BEFORE resolving the runner: its scoped tool dependencies read
            // IUserContext at construction/execution time.
            scope.ServiceProvider.GetRequiredService<DetachedUserContext>().Seed(job);

            var runner = scope.ServiceProvider.GetRequiredService<ITurnRunner>();
            await runner.RunAsync(job, _stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Shutdown - the runner already error-finalised its row best-effort.
        }
        catch (Exception ex)
        {
            // The runner finalises turn-level failures itself; this is the last-ditch guard so a
            // defect can't fault the block (an unhandled delegate exception stops it processing).
            _logger.LogError(
                ex,
                "Turn failed unexpectedly for conversation {ConversationId} in project {Pid}.",
                job.ConversationId,
                job.Pid);
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Cancel in-flight runners (they error-finalise on the shutdown token) and make any queued
        // turns short-circuit, then complete the block and best-effort drain within the host's
        // shutdown window. Queued jobs are dropped to the orphan rule - no graceful drain by design.
        await _stopping.CancelAsync().ConfigureAwait(false);
        _runner.Complete();

        try
        {
            await _runner.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Drain window elapsed - the orphan rule finalises whatever did not error-finalise in time.
        }
    }
}
