using Gert.Service.Chat;

namespace Gert.Api.Chat;

/// <summary>
/// Drains the <see cref="ChannelTurnQueue"/>'s keyed lanes - one loop per shard, all under this
/// ONE hosted service (decisions section 11: the gate index is the 409/seq protection; the lanes
/// are only throughput) - and runs each <see cref="TurnJob"/> through <see cref="ITurnRunner"/>
/// (chat-and-tools.md section detached turns; mirrors <see cref="Ingestion.IngestionWorker"/>). A
/// conversation always hashes to one lane, so its turns never overlap; different conversations may.
/// Opens a <b>fresh DI scope per turn</b> and seeds its <see cref="DetachedUserContext"/> from the
/// job FIRST, so the scoped tools (rag -> per-user databases) resolve with the plan-time identity +
/// entitlement snapshot instead of a request context that does not exist here. The runner finalises
/// its own failures (status=error); the per-job guard exists so a defect can never stop its lane.
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
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, _queue.ShardCount)
            .Select(shard => DrainShardAsync(shard, stoppingToken)));

    private async Task DrainShardAsync(int shard, CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in _queue.ReaderFor(shard).ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await ProcessAsync(job, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown: this lane is done; WhenAll completes when every lane has
            // observed it. In-flight jobs error-finalise best-effort in the
            // runner; queued jobs are dropped to the orphan rule + the planner
            // write-back - no graceful drain by design.
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
            // Shutdown - the runner already error-finalised its row best-effort.
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
