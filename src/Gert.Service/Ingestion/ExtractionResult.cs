namespace Gert.Service.Ingestion;

/// <summary>
/// The outcome of <see cref="ITextExtractor.ExtractAsync"/> - the extracted pages
/// (possibly empty) plus an optional failure reason. The pipeline treats an empty
/// page set OR a non-null <see cref="Error"/> as "no usable text" and marks the
/// document <c>failed</c> (chat-and-tools.md section ingestion step 2; decisions section 5).
/// </summary>
public sealed record ExtractionResult
{
    /// <summary>The pages of extracted text, in source order. Empty when nothing was extracted.</summary>
    public required IReadOnlyList<ExtractedPage> Pages { get; init; }

    /// <summary>
    /// A failure reason when extraction could not run at all (e.g. an extractor that
    /// is not available). Null on success, even if
    /// <see cref="Pages"/> is empty (a scanned PDF with no text layer).
    /// </summary>
    public string? Error { get; init; }

    public bool HasText => Pages.Any(p => !string.IsNullOrWhiteSpace(p.Text));

    public static ExtractionResult FromPages(IReadOnlyList<ExtractedPage> pages) =>
        new() { Pages = pages };

    public static ExtractionResult Failed(string error) =>
        new() { Pages = [], Error = error };
}
