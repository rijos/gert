using Gert.Model;
using Gert.Model.Rag;
using Gert.Service.Database;

namespace Gert.Database.Sqlite;

/// <summary>
/// <see cref="IRagRepository"/> stub.
///
/// <para>
/// SCOPE NOTE (U4a): rag.db uses a <c>vec0</c> virtual table (plus FTS5) that
/// requires the native <b>sqlite-vec</b> extension, wired in U4b. Until then this
/// type exists only so the project compiles; every member throws.
/// TODO U4b: implement against rag.db once the sqlite-vec native extension loads
/// (insert chunks, vec0 KNN, FTS5 BM25, RRF fusion — see U4b acceptance).
/// </para>
/// </summary>
public sealed class SqliteRagRepository : IRagRepository
{
    private const string NotReady =
        "rag.db requires the sqlite-vec native extension; wired in U4b.";

    /// <inheritdoc />
    public Task<IReadOnlyList<Document>> ListDocumentsAsync(
        DocumentKind? kind = null,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(NotReady);

    /// <inheritdoc />
    public Task<Document?> GetDocumentAsync(string documentId, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(NotReady);

    /// <inheritdoc />
    public Task InsertDocumentAsync(Document document, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(NotReady);

    /// <inheritdoc />
    public Task UpdateDocumentAsync(Document document, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(NotReady);

    /// <inheritdoc />
    public Task<bool> DeleteDocumentAsync(string documentId, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(NotReady);

    /// <inheritdoc />
    public Task ClearAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(NotReady);

    /// <inheritdoc />
    public Task InsertChunksAsync(
        IReadOnlyList<ChunkInsert> chunks,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(NotReady);

    /// <inheritdoc />
    public Task<IReadOnlyList<RetrievedChunk>> HybridSearchAsync(
        string query,
        IReadOnlyList<float> queryEmbedding,
        int k,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(NotReady);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
