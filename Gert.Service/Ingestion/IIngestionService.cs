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

/// <summary>
/// One unit of ingestion work (chat-and-tools.md § ingestion). The document row
/// and file already exist; this points the worker at them within the caller's
/// folder.
/// </summary>
public sealed record IngestJob
{
    public required string Iss { get; init; }

    public required string Sub { get; init; }

    public required string Pid { get; init; }

    public required string DocumentId { get; init; }

    /// <summary>Absolute path to the stored upload under the project's <c>files/</c>.</summary>
    public required string FilePath { get; init; }
}

/// <summary>Progress for the doclist pill / "embedding n / m chunks…" hint.</summary>
public sealed record IngestionProgress
{
    public required int ChunksEmbedded { get; init; }

    public required int ChunksTotal { get; init; }
}
