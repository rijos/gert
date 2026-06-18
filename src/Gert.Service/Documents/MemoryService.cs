using System.Text;
using Gert.Chat;
using Gert.Model;
using Gert.Model.Dtos;
using Gert.Model.Rag;
using Gert.Rag;
using Gert.Service.Ingestion;
using Gert.Service.Validation;
using Gert.Storage;

namespace Gert.Service.Documents;

/// <summary>
/// Manages a project's memory entries - markdown notes stored under
/// <c>memory/{id}.md</c> via <see cref="IObjectStore"/> and embedded into the
/// project's <c>rag.db</c> as a <c>kind='memory'</c> document so they ride the same
/// <see cref="IRagStore.HybridSearchAsync"/> as documents (chat-and-tools.md
/// section "memory rides the same query"; configuration.md section 2.3). The body is written
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
    private readonly IRagIndexProvider _databases;
    private readonly IObjectStore _objects;
    private readonly IEmbeddingClient _embeddings;
    private readonly IUserContext _user;
    private readonly TimeProvider _time;
    private readonly ChunkingOptions _chunking;

    private const string MemoryMime = "text/markdown";

    public MemoryService(
        IRagIndexProvider databases,
        IObjectStore objects,
        IEmbeddingClient embeddings,
        IUserContext user,
        TimeProvider time)
    {
        _databases = databases ?? throw new ArgumentNullException(nameof(databases));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
        _user = user ?? throw new ArgumentNullException(nameof(user));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _chunking = ChunkingOptions.Default;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemoryEntry>> ListAsync(
        string pid,
        CancellationToken cancellationToken = default)
    {
        await using var repo = await OpenAsync(pid, cancellationToken).ConfigureAwait(false);
        var rows = await repo.ListDocumentsAsync(DocumentKind.Memory, cancellationToken).ConfigureAwait(false);

        // List projection omits the body (Content == null) - the row carries title,
        // pinned and the timestamp; the body lives in the object store.
        // Ordered by worth: pinned entries (always-injected, human-curated) first,
        // then newest - the repository's row order is storage-incidental.
        return rows.Select(d => new MemoryEntry
        {
            Id = d.Id,
            Title = DecodeTitle(d.Filename),
            Content = null,
            Pinned = d.Pinned,
            UpdatedAt = d.CreatedAt,
        })
        .OrderByDescending(m => m.Pinned)
        .ThenByDescending(m => m.UpdatedAt)
        .ToList();
    }

    /// <inheritdoc />
    public async Task<MemoryEntry> UpsertAsync(
        string pid,
        Validated<CreateMemoryRequest> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var dto = request.Value;

        var id = Guid.NewGuid().ToString("D");
        var pinned = dto.Pinned ?? false;
        // Injected clock (dotnet-style-guide.md section 5) so tests can pin the timestamp.
        var now = _time.GetUtcNow();
        var scope = ScopeFor(pid);
        var key = MemoryKey(id);

        // Failure order (dotnet-style-guide section 8: state it): chunk + embed FIRST -
        // pure in-memory work plus the network call, no disk effects - so an
        // embedding failure aborts before anything is persisted (no Ready-but-
        // unsearchable row, no orphan blob). Only then blob, then rows.
        var chunks = TextChunker.Chunk(
            [new ExtractedPage { Text = dto.Content }],
            _chunking);

        IReadOnlyList<float[]> vectors = [];
        if (chunks.Count > 0)
        {
            vectors = await _embeddings
                .EmbedAsync(chunks.Select(c => c.Content).ToList(), cancellationToken)
                .ConfigureAwait(false);
        }

        // 1. Store the markdown body via the object store (decision: files via IObjectStore).
        var bodyBytes = Encoding.UTF8.GetBytes(dto.Content);
        await using (var body = new MemoryStream(bodyBytes, writable: false))
        {
            await _objects.PutAsync(scope, key, body, cancellationToken).ConfigureAwait(false);
        }

        // 2. Insert the rag.db row (kind='memory', final ChunkCount up front - no
        //    trailing update) + chunks so it is retrievable by search_documents
        //    alongside documents.
        var document = new Document
        {
            Id = id,
            Filename = EncodeTitle(dto.Title),
            Mime = MemoryMime,
            SizeBytes = bodyBytes.LongLength,
            Status = DocumentStatus.Ready,
            ChunkCount = chunks.Count,
            Kind = DocumentKind.Memory,
            Pinned = pinned,
            CreatedAt = now,
        };

        try
        {
            await using var repo = await OpenAsync(pid, cancellationToken).ConfigureAwait(false);
            await repo.InsertDocumentAsync(document, cancellationToken).ConfigureAwait(false);

            if (chunks.Count > 0)
            {
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
            }
        }
        catch
        {
            // Compensate: the row/chunk write failed after the blob landed -
            // remove the blob so no orphan body survives (the inverse of
            // DeleteAsync's row+blob pairing), then rethrow the original failure.
            // CancellationToken.None: cleanup must run even when the failure IS
            // a cancel.
            await _objects.DeleteAsync(scope, key, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return new MemoryEntry
        {
            Id = id,
            Title = dto.Title,
            Content = dto.Content,
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

    private Task<IRagStore> OpenAsync(string pid, CancellationToken cancellationToken) =>
        _databases.OpenAsync(_user.Iss, _user.Sub, pid, cancellationToken);

    private ObjectScope ScopeFor(string pid) => ObjectScope.Project(_user.Iss, _user.Sub, pid);

    /// <summary>The object-store key for a memory body - <c>memory/{id}.md</c>.</summary>
    private static string MemoryKey(string id) => $"memory/{id}.md";

    private static string EncodeTitle(string title) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(title));

    /// <summary>
    /// Decode a base64 <c>documents.filename</c> back to the entry title. Falls
    /// back to the raw value if it does not decode (defensive - one malformed row
    /// must not make the whole list throw; mirrors <c>RagTool.DisplayName</c>).
    /// </summary>
    private static string DecodeTitle(string encoded)
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
