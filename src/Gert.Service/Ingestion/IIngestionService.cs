namespace Gert.Service.Ingestion;

/// <summary>
/// The document ingestion pipeline (chat-and-tools.md § ingestion):
/// extract → chunk → embed → write, with progress, setting <c>status='failed'</c>
/// when there is no extractable text. Run inline by the Console or via the Api's
/// background worker; the heavy extraction step runs out-of-process (U10).
/// </summary>
public interface IIngestionService
{
    /// <summary>
    /// Ingest one already-stored document for a project, reporting progress
    /// (e.g. "embedding 12 / 19 chunks…").
    /// </summary>
    Task IngestAsync(
        IngestJob job,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
