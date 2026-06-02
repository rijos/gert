namespace Gert.Service.Ingestion;

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
