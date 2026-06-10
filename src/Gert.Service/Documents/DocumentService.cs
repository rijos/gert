using System.Text;
using Gert.Model;
using Gert.Model.Rag;
using Gert.Database;
using Gert.Service.Ingestion;
using Gert.Service.Storage;
using Gert.Service.Validation;

namespace Gert.Service.Documents;

/// <summary>
/// Manages a project's RAG documents (rest-api.md § documents), scoped to the
/// caller's identity via <see cref="IUserContext"/> — the caller supplies only the
/// <c>pid</c>, never the user, so a request cannot widen scope to another project
/// (configuration.md § 2.5).
///
/// <para>
/// Upload (chat-and-tools.md § ingestion) stores the bytes through
/// <see cref="IObjectStore"/> under a server-generated <c>{doc-id}.{ext}</c> key —
/// the upload filename is never a storage path — inserts a <c>processing</c> row
/// with the <b>base64-encoded original filename</b> as display metadata, then
/// enqueues an <see cref="IngestJob"/>. The blob is read/written/deleted only
/// through the object store (decision: files via IObjectStore; filename is base64
/// metadata, render-sanitized by the SPA, not a path).
/// </para>
/// </summary>
public sealed class DocumentService : IDocumentService
{
    private readonly IRagDatabaseProvider _databases;
    private readonly IObjectStore _objects;
    private readonly IIngestionQueue _queue;
    private readonly IValidationProvider _validation;
    private readonly IUserContext _user;

    public DocumentService(
        IRagDatabaseProvider databases,
        IObjectStore objects,
        IIngestionQueue queue,
        IValidationProvider validation,
        IUserContext user)
    {
        _databases = databases ?? throw new ArgumentNullException(nameof(databases));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _validation = validation ?? throw new ArgumentNullException(nameof(validation));
        _user = user ?? throw new ArgumentNullException(nameof(user));
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
        DocumentUpload upload,
        CancellationToken cancellationToken = default)
    {
        // 1. Validate at the service boundary (fail-closed): extension allowlist +
        //    size + mime + non-empty. The filename is metadata, not a path — no
        //    path-safety check (decision). Reject before any disk touch.
        var validation = _validation.Validate(upload);
        if (!validation.IsValid)
        {
            throw new ValidationException(validation);
        }

        var documentId = Guid.NewGuid().ToString("D");
        var extension = ValidationRules.ExtensionOf(upload.Filename);
        var key = ObjectKey(documentId, extension);
        var scope = ScopeFor(pid);

        // 2. Store the bytes via the object store (decision: files via IObjectStore
        //    only) under the server-generated {doc-id}.{ext} key. The counting wrapper
        //    records the true written size AND enforces the size cap mid-stream:
        //    both shipped hosts pass a server-measured SizeBytes (already gated by
        //    the validator above), so the streaming cap is defence-in-depth for
        //    callers that cannot know the size up front (SizeBytes == null).
        long sizeBytes;
        await using (var counting = new CountingStream(upload.OpenReadStream(), UploadConstraints.MaxSizeBytes))
        {
            try
            {
                await _objects.PutAsync(scope, key, counting, cancellationToken).ConfigureAwait(false);
            }
            catch (ValidationException)
            {
                // The cap tripped mid-stream: compensate by removing whatever
                // partial blob the backend persisted (DeleteAsync is idempotent;
                // the local store's stage-and-rename usually leaves nothing, but a
                // cloud backend may), then surface the validator-identical 400.
                // CancellationToken.None: best-effort cleanup must still run if
                // the request token is already cancelled.
                await _objects.DeleteAsync(scope, key, CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            sizeBytes = counting.BytesRead;
        }

        // 3. Insert the processing row. filename = Base64(original) — display metadata,
        //    never a path; the SPA decodes + render-sanitizes it (text node + bidi-isolate).
        var document = new Document
        {
            Id = documentId,
            Filename = EncodeFilename(upload.Filename),
            Mime = upload.Mime,
            SizeBytes = upload.SizeBytes ?? sizeBytes,
            Status = DocumentStatus.Processing,
            ChunkCount = 0,
            Kind = DocumentKind.Document,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await using (var repo = await OpenAsync(pid, cancellationToken).ConfigureAwait(false))
        {
            await repo.InsertDocumentAsync(document, cancellationToken).ConfigureAwait(false);
        }

        // 4. Enqueue ingestion (extract → chunk → embed → write) and return the row
        //    immediately (the upload responds 202; the client polls GetAsync).
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

        // Remove the rag rows (repo cascades chunks/vec/fts) AND the blob.
        var removed = await repo.DeleteDocumentAsync(documentId, cancellationToken).ConfigureAwait(false);

        var extension = ValidationRules.ExtensionOf(DecodeFilename(document.Filename));
        await _objects.DeleteAsync(ScopeFor(pid), ObjectKey(documentId, extension), cancellationToken)
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

        // Clear the stored uploads and memory bodies; the project's config
        // sidecar (meta.json) and databases are not touched here.
        await _objects.DeletePrefixAsync(ScopeFor(pid), "files/", cancellationToken)
            .ConfigureAwait(false);
        await _objects.DeletePrefixAsync(ScopeFor(pid), "memory/", cancellationToken)
            .ConfigureAwait(false);
    }

    // ---- helpers -----------------------------------------------------------

    private Task<IRagRepository> OpenAsync(string pid, CancellationToken cancellationToken) =>
        _databases.OpenAsync(_user.Iss, _user.Sub, pid, cancellationToken);

    private ObjectScope ScopeFor(string pid) => ObjectScope.Project(_user.Iss, _user.Sub, pid);

    /// <summary>The server-generated blob key under <c>files/</c>: <c>files/{doc-id}.{ext}</c>.</summary>
    private static string ObjectKey(string documentId, string extension) =>
        extension.Length == 0 ? $"files/{documentId}" : $"files/{documentId}.{extension}";

    /// <summary>Base64 of the UTF-8 original filename — stored as display metadata (decision).</summary>
    private static string EncodeFilename(string filename) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(filename));

    /// <summary>
    /// Decode a base64 <c>documents.filename</c> back to the original (for the
    /// delete-key ext). Falls back to the raw value if it does not decode
    /// (defensive — a malformed row must not make delete throw; mirrors
    /// <c>RagTool.DisplayName</c>).
    /// </summary>
    private static string DecodeFilename(string encoded)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch (FormatException)
        {
            return encoded;
        }
    }
}
