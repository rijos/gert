namespace Gert.Service.Ingestion;

/// <summary>
/// The hand-off seam from upload to the ingestion worker (chat-and-tools.md
/// section ingestion: "enqueue IngestJob"). <see cref="Documents.IDocumentService"/>
/// stores the bytes, inserts the <c>processing</c> row, and enqueues a job here,
/// then returns immediately so the upload responds <c>202</c>.
///
/// <para>
/// The production impl is a <c>System.Threading.Channels</c>-backed queue
/// drained by a <c>BackgroundService</c> that calls
/// <see cref="IIngestionService.IngestAsync"/>. The default registered here
/// (<see cref="InlineIngestionQueue"/>) runs ingestion synchronously so a host
/// without the background worker (and the integration tests) still complete the
/// extract -> chunk -> embed -> write path on upload.
/// </para>
/// </summary>
public interface IIngestionQueue
{
    /// <summary>Enqueue one document for ingestion. Returns once accepted, not once ingested.</summary>
    Task EnqueueAsync(IngestJob job, CancellationToken cancellationToken = default);
}
