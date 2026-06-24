using Gert.Model.Rag;
using Gert.Rag;
using Gert.Service.Documents;
using Gert.Service.Ingestion;
using Gert.Storage;
using Gert.Tools.Resources;

namespace Gert.Agent.Hosting;

/// <summary>
/// The project-scoped <see cref="IDocumentResource"/> (chat-and-tools.md section read_document):
/// lists THIS project's ready documents from <c>rag.db</c> and returns one document's <b>full</b>
/// text by reading its original stored blob (<c>files/{doc-id}</c>) and decoding it as UTF-8 -
/// exact bytes, not lossy reassembled RAG chunks. Pre-scoped to one validated <c>(iss, sub, pid)</c>
/// at construction, so a read structurally cannot reach another user's or project's documents -
/// identity is the host's, never the tool's.
/// </summary>
internal sealed class ProjectDocumentResource : IDocumentResource
{
    /// <summary>The blob key DocumentService stores uploads under (its private <c>ObjectKey</c>): <c>files/{doc-id}</c>.</summary>
    private static string BlobKey(string documentId) => $"files/{documentId}";

    private readonly IRagIndexProvider _databases;
    private readonly IObjectStore _objects;
    private readonly string _iss;
    private readonly string _sub;
    private readonly string _pid;

    public ProjectDocumentResource(
        IRagIndexProvider databases,
        IObjectStore objects,
        string iss,
        string sub,
        string pid)
    {
        _databases = databases ?? throw new ArgumentNullException(nameof(databases));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _iss = iss ?? throw new ArgumentNullException(nameof(iss));
        _sub = sub ?? throw new ArgumentNullException(nameof(sub));
        _pid = pid ?? throw new ArgumentNullException(nameof(pid));
    }

    public async Task<IReadOnlyList<DocumentSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var store = await _databases.OpenAsync(_iss, _sub, _pid, cancellationToken).ConfigureAwait(false);
        var documents = await store.ListDocumentsAsync(cancellationToken).ConfigureAwait(false);

        return documents
            .Where(d => d.Status == DocumentStatus.Ready)
            .OrderByDescending(d => d.CreatedAt)
            .Select(ToSummary)
            .ToList();
    }

    public async Task<DocumentContent?> ReadAsync(
        string docRef,
        int offset,
        int maxChars,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(docRef);

        await using var store = await _databases.OpenAsync(_iss, _sub, _pid, cancellationToken).ConfigureAwait(false);
        var documents = await store.ListDocumentsAsync(cancellationToken).ConfigureAwait(false);

        var match = Resolve(documents, docRef);
        if (match is null)
        {
            return null; // not found or ambiguous - the caller lists the candidates.
        }

        var title = StoredFilenames.Decode(match.Filename);

        // Read the original blob exactly. A missing blob (e.g. a half-deleted document) reads as
        // "not text" rather than throwing into the tool call.
        byte[] bytes;
        try
        {
            await using var blob = await _objects
                .OpenReadAsync(ObjectScope.Project(_iss, _sub, _pid), BlobKey(match.Id), cancellationToken)
                .ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await blob.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            bytes = buffer.ToArray();
        }
        catch (FileNotFoundException)
        {
            return Binary(title);
        }

        if (!TextContent.TryDecode(bytes, out var text))
        {
            return Binary(title);
        }

        var total = text.Length;
        var from = Math.Clamp(offset, 0, total);
        var take = Math.Clamp(maxChars, 0, total - from);
        var slice = text.Substring(from, take);

        return new DocumentContent
        {
            Title = title,
            IsText = true,
            Content = slice,
            TotalChars = total,
            Offset = from,
            HasMore = from + take < total,
        };
    }

    /// <summary>
    /// Resolve a reference to one document: exact title, else a single case-insensitive title,
    /// else exact id. Returns null when nothing matches or a title is ambiguous (so the caller
    /// can list the candidates and the model can pick an exact name).
    /// </summary>
    private static Document? Resolve(IReadOnlyList<Document> documents, string docRef)
    {
        var ready = documents.Where(d => d.Status == DocumentStatus.Ready).ToList();

        var exact = ready.Where(d => StoredFilenames.Decode(d.Filename) == docRef).ToList();
        if (exact.Count == 1)
        {
            return exact[0];
        }

        if (exact.Count == 0)
        {
            var ci = ready
                .Where(d => string.Equals(StoredFilenames.Decode(d.Filename), docRef, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (ci.Count == 1)
            {
                return ci[0];
            }
        }

        return ready.FirstOrDefault(d => d.Id == docRef);
    }

    private static DocumentContent Binary(string title) => new()
    {
        Title = title,
        IsText = false,
        Content = string.Empty,
        TotalChars = 0,
        Offset = 0,
        HasMore = false,
    };

    private static DocumentSummary ToSummary(Document d) => new()
    {
        Id = d.Id,
        Title = StoredFilenames.Decode(d.Filename),
        Mime = d.Mime,
        SizeBytes = d.SizeBytes,
    };
}
