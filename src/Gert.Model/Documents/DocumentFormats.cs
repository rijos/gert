namespace Gert.Model.Documents;

/// <summary>
/// The document formats whose bytes are <b>binary</b> and must be parsed only inside the
/// hardened, isolated extraction subprocess (security F7) - never decoded as text in-process.
/// Every other upload is treated as a candidate text file: it is UTF-8 decoded by the plain-text
/// extractor, which fails the document if the bytes are not text (chat-and-tools.md section
/// ingestion). The single source of truth shared by the upload constraints and the extractor
/// routing, so "is this a binary document format?" is answered in one place.
/// </summary>
public static class DocumentFormats
{
    /// <summary>The binary document extensions (lowercase, no dot) routed to the isolated extractor.</summary>
    public static readonly IReadOnlySet<string> IsolatedExtensions =
        new HashSet<string>(StringComparer.Ordinal) { "pdf", "docx", "xlsx" };

    /// <summary>True if <paramref name="extension"/> is a binary document format (isolated parsing).</summary>
    public static bool IsIsolated(string? extension) =>
        extension is not null && IsolatedExtensions.Contains(extension);
}
