using Gert.Model;
using Gert.Model.Rag;

namespace Gert.Rag;

/// <summary>
/// Per-project RAG index persistence - documents, chunks, the vector index and the
/// lexical index (storage-and-data.md section rag.db). One instance is scoped to a
/// single project; dispose it when the unit of work completes. Hybrid retrieval fuses
/// vector KNN + BM25 via RRF (chat-and-tools.md section hybrid retrieval). This is the
/// RAG capability's port (engine-neutral): the SQLite impl is sqlite-vec + FTS5, but a
/// dedicated vector store (Qdrant, pgvector, ...) is a sibling engine behind the same
/// contract.
/// </summary>
public interface IRagStore : IAsyncDisposable
{
    Task<IReadOnlyList<Document>> ListDocumentsAsync(
        CancellationToken cancellationToken = default);

    Task<Document?> GetDocumentAsync(string documentId, CancellationToken cancellationToken = default);

    Task InsertDocumentAsync(Document document, CancellationToken cancellationToken = default);

    Task UpdateDocumentAsync(Document document, CancellationToken cancellationToken = default);

    Task<bool> DeleteDocumentAsync(string documentId, CancellationToken cancellationToken = default);

    /// <summary>Wipe every document, chunk, vec and fts row (forget-documents).</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);

    Task InsertChunksAsync(
        IReadOnlyList<ChunkInsert> chunks,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete every chunk (plus its vec/fts satellite rows) belonging to
    /// <paramref name="documentId"/>, keeping the document row itself - the
    /// ingestion failure path's compensation: batches commit per batch, so a
    /// mid-pipeline failure can leave chunks behind; a <c>failed</c> document must
    /// leave nothing retrievable (chat-and-tools.md section ingestion).
    /// </summary>
    Task DeleteChunksAsync(string documentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RetrievedChunk>> HybridSearchAsync(
        string query,
        IReadOnlyList<float> queryEmbedding,
        int k,
        CancellationToken cancellationToken = default);
}
