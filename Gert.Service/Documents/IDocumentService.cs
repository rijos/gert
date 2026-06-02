using Gert.Model.Rag;

namespace Gert.Service.Documents;

/// <summary>
/// Manages a project's RAG documents (rest-api.md § documents). Upload stores
/// the file, inserts a <c>processing</c> row, and enqueues ingestion; the client
/// polls <see cref="GetAsync"/> for the <c>processing → ready/failed</c>
/// transition (chat-and-tools.md § ingestion).
/// </summary>
public interface IDocumentService
{
    /// <summary>List documents for the doclist (name, size, chunk_count, status, error).</summary>
    Task<IReadOnlyList<Document>> ListAsync(
        string pid,
        CancellationToken cancellationToken = default);

    /// <summary>Get one document's current status/progress (polled while processing).</summary>
    Task<Document?> GetAsync(
        string pid,
        string documentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Accept an upload: store the bytes, insert a <c>processing</c> row, enqueue
    /// ingestion, and return the row immediately.
    /// </summary>
    Task<Document> UploadAsync(
        string pid,
        DocumentUpload upload,
        CancellationToken cancellationToken = default);

    /// <summary>Delete a document, its chunks/vec/fts rows, and the original file.</summary>
    Task<bool> DeleteAsync(
        string pid,
        string documentId,
        CancellationToken cancellationToken = default);

    /// <summary>Wipe a project's whole RAG corpus + files, keeping its chats (forget-documents).</summary>
    Task ForgetAllAsync(
        string pid,
        CancellationToken cancellationToken = default);
}
