namespace Gert.Service.Ingestion;

/// <summary>
/// The default <see cref="IIngestionQueue"/> - runs <see cref="IIngestionService.IngestAsync"/>
/// inline (synchronously, awaited) on enqueue. This keeps the upload -> ingest path
/// complete for hosts without the background worker and for the integration
/// tests (after <c>UploadAsync</c> the document is already <c>ready</c>/<c>failed</c>).
///
/// <para>
/// The API host registers a <c>System.Threading.Channels</c>-backed queue + a
/// <c>BackgroundService</c> in its place (one DI swap) so the production upload
/// responds <c>202</c> while ingestion runs off-thread. The ingestion service never
/// throws out of the worker path (it records <c>failed</c>), so an inline run cannot
/// fail the upload either.
/// </para>
/// </summary>
public sealed class InlineIngestionQueue : IIngestionQueue
{
    private readonly IIngestionService _ingestion;

    public InlineIngestionQueue(IIngestionService ingestion)
    {
        _ingestion = ingestion ?? throw new ArgumentNullException(nameof(ingestion));
    }

    /// <inheritdoc />
    public Task EnqueueAsync(IngestJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        return _ingestion.IngestAsync(job, progress: null, cancellationToken);
    }
}
