namespace Gert.Service.Ingestion;

/// <summary>
/// The text-extraction port - ingestion step 1 (chat-and-tools.md section ingestion).
/// Turns the raw bytes of an upload into <see cref="ExtractedPage"/>s the pipeline
/// can chunk + embed. The implementation is chosen by file type:
/// <list type="bullet">
///   <item>any non-binary type -> a plain-text extractor (UTF-8 decode; fails if not text).</item>
///   <item>pdf/docx/xlsx -> an isolated-subprocess extractor (security F7).</item>
/// </list>
/// Both leaves land in the <c>Gert.Ingestion</c> adapter; hosts without it see every
/// type return <see cref="ExtractionResult.Failed"/> and the doc marked <c>failed</c>.
/// </summary>
public interface ITextExtractor
{
    /// <summary>
    /// True if this extractor handles <paramref name="extension"/> (lowercase, no
    /// dot - e.g. <c>"md"</c>, <c>"pdf"</c>). A composite extractor uses this to
    /// route by type.
    /// </summary>
    bool CanExtract(string extension);

    /// <summary>
    /// Extract text from <paramref name="content"/> for a blob of the given
    /// <paramref name="extension"/>. Never throws for an unextractable input -
    /// returns <see cref="ExtractionResult.Failed"/> or an empty page set so the
    /// pipeline (not the caller) decides the document is <c>failed</c>. The caller
    /// owns and disposes <paramref name="content"/>.
    /// </summary>
    Task<ExtractionResult> ExtractAsync(
        Stream content,
        string extension,
        CancellationToken cancellationToken = default);
}
