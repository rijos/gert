using Gert.Service.Chat;

namespace Gert.Api.Chat;

/// <summary>
/// Drains the <see cref="ChannelTurnQueue"/> and runs each <see cref="TurnJob"/>
/// through <see cref="ITurnRunner"/> (chat-and-tools.md § detached turns; mirrors
/// <see cref="Ingestion.IngestionWorker"/>). Opens a <b>fresh DI scope per
/// turn</b> and seeds its <see cref="DetachedUserContext"/> from the job FIRST,
/// so the scoped tools (rag → per-user databases) resolve with the plan-time
/// identity + entitlement snapshot instead of a request context that does not
/// exist here. The runner finalises its own failures (status=error); the loop's
/// guard exists so a defect can never stop the worker.
/// </summary>
public sealed class TurnWorker : BackgroundService
{
    private readonly ChannelTurnQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TurnWorker> _logger;

    public TurnWorker(
        ChannelTurnQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<TurnWorker> logger)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            await ProcessAsync(job, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessAsync(TurnJob job, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();

            // Seed identity BEFORE resolving the runner: its scoped tool
            // dependencies read IUserContext at construction/execution time.
            scope.ServiceProvider.GetRequiredService<DetachedUserContext>().Seed(job);

            var runner = scope.ServiceProvider.GetRequiredService<ITurnRunner>();
            await runner.RunAsync(job, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown — the runner already error-finalised its row best-effort.
        }
        catch (Exception ex)
        {
            // The runner finalises turn-level failures itself; this is the
            // last-ditch guard so a defect can't kill the worker loop.
            _logger.LogError(
                ex,
                "Turn failed unexpectedly for conversation {ConversationId} in project {Pid}.",
                job.ConversationId,
                job.Pid);
        }
    }
}
