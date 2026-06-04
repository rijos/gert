namespace Gert.Service.Ingestion;

/// <summary>
/// One unit of extracted text from a source document, with an optional locator
/// (chat-and-tools.md § ingestion step 1). A plain md/txt extract yields a single
/// page with a null <see cref="Locator"/>; a PDF extractor (U10) yields one per
/// page with <c>"p.N"</c>, a DOCX extractor a section locator. Chunking carries
/// the locator through to <c>chunks.page</c> for citations.
/// </summary>
public sealed record ExtractedPage
{
    /// <summary>The extracted text for this page/section.</summary>
    public required string Text { get; init; }

    /// <summary>Source locator — <c>"p.4"</c>, <c>"§3"</c> — or null when the format has none.</summary>
    public string? Locator { get; init; }
}
