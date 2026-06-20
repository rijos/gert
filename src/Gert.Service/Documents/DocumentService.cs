using Gert.Model;
using Gert.Model.Documents;
using Gert.Model.Rag;
using Gert.Rag;
using Gert.Service.Ingestion;
using Gert.Service.Storage;
using Gert.Storage;
using Gert.Validation;

namespace Gert.Service.Documents;

/// <summary>
/// Manages a project's RAG documents (rest-api.md section documents), scoped to the
/// caller's identity via <see cref="IUserContext"/> - the caller supplies only the
/// <c>pid</c>, never the user, so a request cannot widen scope to another project
/// (configuration.md section 2.5).
///
/// <para>
/// Upload (chat-and-tools.md section ingestion) stores the bytes through
/// <see cref="IObjectStore"/> under a fully server-generated <c>files/{doc-id}</c> key -
/// the doc-id is a server UUID, so <b>nothing the caller supplies ever reaches a storage
/// path or carries an extension</b>. The base64-encoded original filename is kept only as
/// display metadata in the database (render-sanitized by the SPA), and the file type
/// drives extraction off the in-memory <see cref="IngestJob.Extension"/>, never the
/// stored key.
/// </para>
/// </summary>
public sealed class DocumentService : IDocumentService
{
    private readonly IRagIndexProvider _databases;
    private readonly IObjectStore _objects;
    private readonly IIngestionQueue _queue;
    private readonly IUserContext _user;
    private readonly TimeProvider _time;

    public DocumentService(
        IRagIndexProvider databases,
        IObjectStore objects,
        IIngestionQueue queue,
        IUserContext user,
        TimeProvider time)
    {
        _databases = databases ?? throw new ArgumentNullException(nameof(databases));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _user = user ?? throw new ArgumentNullException(nameof(user));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Document>> ListAsync(
        string pid,
        CancellationToken cancellationToken = default)
    {
        await using var repo = await OpenAsync(pid, cancellationToken).ConfigureAwait(false);
        return await repo.ListDocumentsAsync(DocumentKind.Document, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Document?> GetAsync(
        string pid,
        string documentId,
        CancellationToken cancellationToken = default)
    {
        await using var repo = await OpenAsync(pid, cancellationToken).ConfigureAwait(false);
        var document = await repo.GetDocumentAsync(documentId, cancellationToken).ConfigureAwait(false);

        // Only documents are surfaced here; memory is GetAsync'd via IMemoryService.
        return document is { Kind: DocumentKind.Document } ? document : null;
    }

    /// <inheritdoc />
    public async Task<Document> UploadAsync(
        string pid,
        Validated<DocumentUpload> upload,
        CancellationToken cancellationToken = default)
    {
        // The filename is metadata, not a path - no path-safety check (decision).
        ArgumentNullException.ThrowIfNull(upload);
        var dto = upload.Value;

        var documentId = Guid.NewGuid().ToString("D");
        // The extension is derived only to (a) route extraction and (b) preserve the
        // original name in the DB - it is NEVER part of the storage key.
        var extension = ValidationRules.ExtensionOf(dto.Filename);
        var key = ObjectKey(documentId);
        var scope = ScopeFor(pid);

        // The counting wrapper records the true written size AND enforces the size cap
        // mid-stream: both shipped hosts pass a server-measured SizeBytes (already gated
        // by the validator above), so the streaming cap is defence-in-depth for callers
        // that cannot know the size up front (SizeBytes == null).
        long sizeBytes;
        await using (var counting = new CountingStream(dto.OpenReadStream(), UploadConstraints.MaxSizeBytes))
        {
            try
            {
                await _objects.PutAsync(scope, key, counting, cancellationToken).ConfigureAwait(false);
            }
            catch (ValidationException)
            {
                // The cap tripped mid-stream: compensate by removing whatever partial blob
                // the backend persisted (idempotent; the local store's stage-and-rename
                // usually leaves nothing, but a cloud backend may), then surface the
                // validator-identical 400. CancellationToken.None: best-effort cleanup must
                // still run if the request token is already cancelled.
                await _objects.DeleteAsync(scope, key, CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            sizeBytes = counting.BytesRead;
        }

        // filename = Base64(original) - display metadata, never a path; the SPA decodes
        // + render-sanitizes it (text node + bidi-isolate).
        var document = new Document
        {
            Id = documentId,
            Filename = StoredFilenames.Encode(dto.Filename),
            Mime = dto.Mime,
            SizeBytes = dto.SizeBytes ?? sizeBytes,
            Status = DocumentStatus.Processing,
            ChunkCount = 0,
            Kind = DocumentKind.Document,
            CreatedAt = _time.GetUtcNow(),
        };

        await using (var repo = await OpenAsync(pid, cancellationToken).ConfigureAwait(false))
        {
            await repo.InsertDocumentAsync(document, cancellationToken).ConfigureAwait(false);
        }

        // Enqueue ingestion (extract -> chunk -> embed -> write) and return the row
        // immediately (the upload responds 202; the client polls GetAsync).
        await _queue.EnqueueAsync(
            new IngestJob
            {
                Iss = _user.Iss,
                Sub = _user.Sub,
                Pid = pid,
                DocumentId = documentId,
                ObjectKey = key,
                Extension = extension,
            },
            cancellationToken).ConfigureAwait(false);

        return document;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        string pid,
        string documentId,
        CancellationToken cancellationToken = default)
    {
        await using var repo = await OpenAsync(pid, cancellationToken).ConfigureAwait(false);

        var document = await repo.GetDocumentAsync(documentId, cancellationToken).ConfigureAwait(false);
        if (document is not { Kind: DocumentKind.Document })
        {
            return false;
        }

        // Remove the rag rows (repo cascades chunks/vec/fts) AND the blob. The key is
        // server-derived from the doc-id alone, so deletion needs no stored filename.
        var removed = await repo.DeleteDocumentAsync(documentId, cancellationToken).ConfigureAwait(false);

        await _objects.DeleteAsync(ScopeFor(pid), ObjectKey(documentId), cancellationToken)
            .ConfigureAwait(false);

        return removed;
    }

    /// <inheritdoc />
    public async Task ForgetAllAsync(
        string pid,
        CancellationToken cancellationToken = default)
    {
        // Wipe the project's whole RAG corpus + its file blobs, keeping chats.
        await using (var repo = await OpenAsync(pid, cancellationToken).ConfigureAwait(false))
        {
            await repo.ClearAsync(cancellationToken).ConfigureAwait(false);
        }

        // Clear the stored uploads and memory bodies; the project's registry row
        // (user.db) and databases are not touched here.
        await _objects.DeletePrefixAsync(ScopeFor(pid), "files/", cancellationToken)
            .ConfigureAwait(false);
        await _objects.DeletePrefixAsync(ScopeFor(pid), "memory/", cancellationToken)
            .ConfigureAwait(false);
    }

    private Task<IRagStore> OpenAsync(string pid, CancellationToken cancellationToken) =>
        _databases.OpenAsync(_user.Iss, _user.Sub, pid, cancellationToken);

    private ObjectScope ScopeFor(string pid) => ObjectScope.Project(_user.Iss, _user.Sub, pid);

    /// <summary>
    /// The fully server-generated blob key under <c>files/</c>: <c>files/{doc-id}</c>. The
    /// doc-id is a server UUID and carries no extension, so the caller's filename never
    /// reaches a storage path (it lives only in <c>documents.filename</c>).
    /// </summary>
    private static string ObjectKey(string documentId) => $"files/{documentId}";
}
