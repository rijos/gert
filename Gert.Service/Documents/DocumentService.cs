using Gert.Model.Rag;

namespace Gert.Service.Documents;

/// <summary>
/// Stub. The full RAG document lifecycle (store bytes, insert a <c>processing</c>
/// row, enqueue ingestion, status polling) lands in U4b (rag.db repo) + U7d
/// (ingestion). Present so <see cref="GertServices"/> + DI compile for the M1
/// conversations/messages gate.
/// </summary>
public sealed class DocumentService : IDocumentService
{
    /// <inheritdoc />
    public Task<IReadOnlyList<Document>> ListAsync(
        string pid,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO U4b/U7d");

    /// <inheritdoc />
    public Task<Document?> GetAsync(
        string pid,
        string documentId,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO U4b/U7d");

    /// <inheritdoc />
    public Task<Document> UploadAsync(
        string pid,
        DocumentUpload upload,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO U4b/U7d");

    /// <inheritdoc />
    public Task<bool> DeleteAsync(
        string pid,
        string documentId,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO U4b/U7d");

    /// <inheritdoc />
    public Task ForgetAllAsync(
        string pid,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO U4b/U7d");
}
