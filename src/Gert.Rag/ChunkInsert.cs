namespace Gert.Rag;

/// <summary>
/// A chunk plus its embedding vector, written together into <c>chunks</c> +
/// <c>vec_chunks</c> + <c>fts_chunks</c> (the three share an integer rowid).
/// </summary>
public sealed record ChunkInsert
{
    public required string DocumentId { get; init; }

    public required int Ordinal { get; init; }

    public required string Content { get; init; }

    public string? Page { get; init; }

    public int? TokenCount { get; init; }

    /// <summary>Embedding vector; dimension must match the index (bge-m3 = 1024).</summary>
    public required IReadOnlyList<float> Embedding { get; init; }
}
