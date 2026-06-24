namespace Gert.Tools.Resources;

/// <summary>
/// Read-only access to a project's whole documents (chat-and-tools.md section read_document),
/// pre-scoped by the host. Complements <see cref="IRagResource"/>: where RAG returns ranked
/// passages for a query, this lists the project's ready documents and returns one document's
/// <b>full</b> text (read from the original stored blob, decoded as UTF-8) so the model can
/// transform an entire file - the use case RAG snippets cannot serve. The tool names neither an
/// identity nor a storage key; the host owns both, so a read structurally cannot reach another
/// user's or project's documents.
/// </summary>
public interface IDocumentResource
{
    /// <summary>The project's ready (extractable) documents, newest first; empty when there are none.</summary>
    Task<IReadOnlyList<DocumentSummary>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Read up to <paramref name="maxChars"/> characters of the document identified by
    /// <paramref name="docRef"/> (exact title, then case-insensitive title, then id), starting at
    /// <paramref name="offset"/>. Returns <c>null</c> when no document matches or the reference is
    /// ambiguous (the caller then lists the candidates). A binary document yields a
    /// <see cref="DocumentContent"/> with <see cref="DocumentContent.IsText"/> false.
    /// </summary>
    Task<DocumentContent?> ReadAsync(
        string docRef,
        int offset,
        int maxChars,
        CancellationToken cancellationToken = default);
}
