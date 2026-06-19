using Gert.Service.Ingestion;
using Microsoft.Extensions.DependencyInjection;

namespace Gert.Api.Ingestion;

/// <summary>
/// Drains the <see cref="ChannelIngestionQueue"/> and runs each <see cref="IngestJob"/>
/// through <see cref="IIngestionService"/> in a <b>fresh DI scope per job</b>, so the
/// scoped ingestion service (and its db/object-store seams) resolve off the request
/// thread - the job carries <c>(iss, sub, pid)</c>, so no request <c>IUserContext</c> is
/// needed. The service fails the document on its own errors (<c>status='failed'</c>); the
/// loop also guards each job so one bad document can never stop the worker.
/// </summary>
public sealed class IngestionWorker : BackgroundService
{
    private readonly ChannelIngestionQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<IngestionWorker> _logger;

    public IngestionWorker(
        ChannelIngestionQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<IngestionWorker> logger)
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

    private async Task ProcessAsync(IngestJob job, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var ingestion = scope.ServiceProvider.GetRequiredService<IIngestionService>();
            await ingestion.IngestAsync(job, progress: null, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown - stop quietly.
        }
        catch (Exception ex)
        {
            // The ingestion service already fails the document on its own errors; this
            // is the last-ditch guard so a defect can't kill the worker loop.
            _logger.LogError(
                ex,
                "Ingestion job failed unexpectedly for document {DocumentId} in project {Pid}.",
                job.DocumentId,
                job.Pid);
        }
    }
}
