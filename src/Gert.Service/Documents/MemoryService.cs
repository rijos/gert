using System.Text;
using Gert.Model;
using Gert.Model.Dtos;
using Gert.Model.Rag;
using Gert.Database;
using Gert.Service.External;
using Gert.Service.Ingestion;
using Gert.Service.Storage;
using Gert.Service.Validation;

namespace Gert.Service.Documents;

/// <summary>
/// Manages a project's memory entries — markdown notes stored under
/// <c>memory/{id}.md</c> via <see cref="IObjectStore"/> and embedded into the
/// project's <c>rag.db</c> as a <c>kind='memory'</c> document so they ride the same
/// <see cref="IRagRepository.HybridSearchAsync"/> as documents (chat-and-tools.md
/// § "memory rides the same query"; configuration.md § 2.3). The body is written
/// and removed only through the object store (decision: files via IObjectStore); the
/// entry title is kept as base64 display metadata in <c>documents.filename</c>,
/// mirroring the document path.
///
/// <para>
/// Each upsert creates a new entry (the DTO carries no id); editing is add + delete
/// at the host. Embedding mirrors the ingestion pipeline but inline and small:
/// the markdown is windowed, embedded, and written as chunks in one pass.
/// </para>
/// </summary>
public sealed class MemoryService : IMemoryService
{
    private readonly IRagDatabaseProvider _databases;
    private readonly IObjectStore _objects;
    private readonly IEmbeddingClient _embeddings;
    private readonly IValidationProvider _validation;
    private readonly IUserContext _user;
    private readonly ChunkingOptions _chunking;

    private const string MemoryMime = "text/markdown";

    public MemoryService(
        IRagDatabaseProvider databases,
        IObjectStore objects,
        IEmbeddingClient embeddings,
        IValidationProvider validation,
        IUserContext user)
    {
        _databases = databases ?? throw new ArgumentNullException(nameof(databases));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
        _validation = validation ?? throw new ArgumentNullException(nameof(validation));
        _user = user ?? throw new ArgumentNullException(nameof(user));
        _chunking = ChunkingOptions.Default;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemoryEntry>> ListAsync(
        string pid,
        CancellationToken cancellationToken = default)
    {
        await using var repo = await OpenAsync(pid, cancellationToken).ConfigureAwait(false);
        var rows = await repo.ListDocumentsAsync(DocumentKind.Memory, cancellationToken).ConfigureAwait(false);

        // List projection omits the body (Content == null) — the row carries title,
        // pinned and the timestamp; the body lives in the object store.
        return rows.Select(d => new MemoryEntry
        {
            Id = d.Id,
            Title = DecodeTitle(d.Filename),
            Content = null,
            Pinned = d.Pinned,
            UpdatedAt = d.CreatedAt,
        }).ToList();
    }

    /// <inheritdoc />
    public async Task<MemoryEntry> UpsertAsync(
        string pid,
        CreateMemoryRequest request,
        CancellationToken cancellationToken = default)
    {
        // Validate at the boundary (fail-closed) before any disk touch.
        var validation = _validation.Validate(request);
        if (!validation.IsValid)
        {
            throw new ValidationException(validation);
        }

        var id = Guid.NewGuid().ToString("D");
        var pinned = request.Pinned ?? false;
        var now = DateTimeOffset.UtcNow;
        var scope = ScopeFor(pid);
        var key = MemoryKey(id);

        // 1. Store the markdown body via the object store (decision: files via IObjectStore).
        var bodyBytes = Encoding.UTF8.GetBytes(request.Content);
        await using (var body = new MemoryStream(bodyBytes, writable: false))
        {
            await _objects.PutAsync(scope, key, body, cancellationToken).ConfigureAwait(false);
        }

        // 2. (Re)embed the body into rag.db as a kind='memory' document + chunks so it
        //    is retrievable by search_documents alongside documents.
        var document = new Document
        {
            Id = id,
            Filename = EncodeTitle(request.Title),
            Mime = MemoryMime,
            SizeBytes = bodyBytes.LongLength,
            Status = DocumentStatus.Ready,
            ChunkCount = 0,
            Kind = DocumentKind.Memory,
            Pinned = pinned,
            CreatedAt = now,
        };

        var chunks = TextChunker.Chunk(
            [new ExtractedPage { Text = request.Content }],
            _chunking);

        await using (var repo = await OpenAsync(pid, cancellationToken).ConfigureAwait(false))
        {
            await repo.InsertDocumentAsync(document, cancellationToken).ConfigureAwait(false);

            if (chunks.Count > 0)
            {
                var vectors = await _embeddings
                    .EmbedAsync(chunks.Select(c => c.Content).ToList(), cancellationToken)
                    .ConfigureAwait(false);

                var inserts = chunks.Select((c, i) => new ChunkInsert
                {
                    DocumentId = id,
                    Ordinal = c.Ordinal,
                    Content = c.Content,
                    Page = c.Locator,
                    TokenCount = c.TokenCount,
                    Embedding = vectors[i],
                }).ToList();

                await repo.InsertChunksAsync(inserts, cancellationToken).ConfigureAwait(false);
                await repo.UpdateDocumentAsync(
                    document with { ChunkCount = chunks.Count },
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return new MemoryEntry
        {
            Id = id,
            Title = request.Title,
            Content = request.Content,
            Pinned = pinned,
            UpdatedAt = now,
        };
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        string pid,
        string memoryId,
        CancellationToken cancellationToken = default)
    {
        await using var repo = await OpenAsync(pid, cancellationToken).ConfigureAwait(false);

        var document = await repo.GetDocumentAsync(memoryId, cancellationToken).ConfigureAwait(false);
        if (document is not { Kind: DocumentKind.Memory })
        {
            return false;
        }

        var removed = await repo.DeleteDocumentAsync(memoryId, cancellationToken).ConfigureAwait(false);
        await _objects.DeleteAsync(ScopeFor(pid), MemoryKey(memoryId), cancellationToken).ConfigureAwait(false);
        return removed;
    }

    // ---- helpers -----------------------------------------------------------

    private Task<IRagRepository> OpenAsync(string pid, CancellationToken cancellationToken) =>
        _databases.OpenAsync(_user.Iss, _user.Sub, pid, cancellationToken);

    private ObjectScope ScopeFor(string pid) => ObjectScope.Project(_user.Iss, _user.Sub, pid);

    /// <summary>The object-store key for a memory body — <c>memory/{id}.md</c>.</summary>
    private static string MemoryKey(string id) => $"memory/{id}.md";

    private static string EncodeTitle(string title) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(title));

    private static string DecodeTitle(string encoded) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
}
