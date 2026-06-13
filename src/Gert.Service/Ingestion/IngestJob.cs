namespace Gert.Service.Ingestion;

/// <summary>
/// One unit of ingestion work (chat-and-tools.md section ingestion). The document row
/// and file already exist; this points the worker at them within the caller's
/// folder.
/// </summary>
public sealed record IngestJob
{
    public required string Iss { get; init; }

    public required string Sub { get; init; }

    public required string Pid { get; init; }

    public required string DocumentId { get; init; }

    /// <summary>
    /// The object-store key of the stored upload within the project's <c>files/</c>
    /// (e.g. <c>{doc-id}.pdf</c>). Files are addressed only via
    /// <see cref="Storage.IObjectStore"/> - never a raw path - so the worker opens
    /// the blob with <c>OpenReadAsync(scope, key)</c> (decision: files via IObjectStore).
    /// </summary>
    public required string ObjectKey { get; init; }

    /// <summary>Lowercase file extension (no dot) routing the text extractor - <c>"md"</c>, <c>"pdf"</c>.</summary>
    public required string Extension { get; init; }
}
