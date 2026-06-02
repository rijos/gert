using Gert.Model;
using Gert.Model.Rag;

namespace Gert.Service.Database;

/// <summary>
/// Per-project <c>rag.db</c> persistence — documents, memory, chunks, the
/// <c>vec0</c> vector index, and the FTS5 lexical index (storage-and-data.md
/// § rag.db). One instance wraps an open connection to a single project's
/// database; dispose it when the unit of work completes. Hybrid retrieval fuses
/// vector KNN + BM25 via RRF (chat-and-tools.md § hybrid retrieval).
/// </summary>
public interface IRagRepository : IAsyncDisposable
{
    // Documents / memory rows
    Task<IReadOnlyList<Document>> ListDocumentsAsync(
        DocumentKind? kind = null,
        CancellationToken cancellationToken = default);

    Task<Document?> GetDocumentAsync(string documentId, CancellationToken cancellationToken = default);

    Task InsertDocumentAsync(Document document, CancellationToken cancellationToken = default);

    Task UpdateDocumentAsync(Document document, CancellationToken cancellationToken = default);

    Task<bool> DeleteDocumentAsync(string documentId, CancellationToken cancellationToken = default);

    /// <summary>Wipe every document, chunk, vec and fts row (forget-documents).</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);

    // Chunks + embeddings (ingestion writes these in step 5)
    Task InsertChunksAsync(
        IReadOnlyList<ChunkInsert> chunks,
        CancellationToken cancellationToken = default);

    // Retrieval — vector KNN + lexical BM25 fused with RRF
    Task<IReadOnlyList<RetrievedChunk>> HybridSearchAsync(
        string query,
        IReadOnlyList<float> queryEmbedding,
        int k,
        CancellationToken cancellationToken = default);
}

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

/// <summary>
/// A fused hybrid-search hit — the <see cref="Chunk"/> joined back to its
/// <see cref="Document"/>, with the RRF score that seeds a citation.
/// </summary>
public sealed record RetrievedChunk
{
    public required Chunk Chunk { get; init; }

    public required Document Document { get; init; }

    /// <summary>The fused RRF score (the 0.89 / 0.81 the mockup shows).</summary>
    public required double Score { get; init; }
}
