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
