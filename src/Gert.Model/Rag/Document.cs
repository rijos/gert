namespace Gert.Model.Rag;

/// <summary>
/// A RAG corpus entry - an uploaded document - mirrors the <c>documents</c> row in a
/// project's <c>rag.db</c> (storage-and-data.md section rag.db; configuration.md section 2.3).
/// </summary>
public sealed record Document
{
    /// <summary>UUID primary key.</summary>
    public required string Id { get; init; }

    public required string Filename { get; init; }

    public required string Mime { get; init; }

    public required long SizeBytes { get; init; }

    public required DocumentStatus Status { get; init; }

    public int ChunkCount { get; init; }

    /// <summary>Failure reason, e.g. "no extractable text".</summary>
    public string? Error { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}
