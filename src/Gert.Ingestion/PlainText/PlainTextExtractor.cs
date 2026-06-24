using Gert.Model.Documents;
using Gert.Service.Ingestion;

namespace Gert.Ingestion.PlainText;

/// <summary>
/// The universal text <see cref="ITextExtractor"/> - reads the upload stream as UTF-8 text
/// (chat-and-tools.md section ingestion). Gert accepts any file, so this is the fallback for
/// <b>every</b> type except the binary document formats
/// (<see cref="DocumentFormats.IsolatedExtensions"/>: pdf/docx/xlsx), which the hardened
/// isolated-subprocess extractor (security F7) handles instead.
/// <para>
/// Type safety lives here, not at the upload gate: <see cref="TextContent.TryDecode"/> sniffs the
/// bytes, so a binary file uploaded with an innocent name yields no usable text and the pipeline
/// marks the document <c>failed</c> ("not a text file"). An empty/whitespace-only file likewise
/// yields no text and is marked <c>failed</c>.
/// </para>
/// </summary>
public sealed class PlainTextExtractor : ITextExtractor
{
    /// <inheritdoc />
    public bool CanExtract(string extension) =>
        !DocumentFormats.IsIsolated(extension);

    /// <inheritdoc />
    public async Task<ExtractionResult> ExtractAsync(
        Stream content,
        string extension,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (!CanExtract(extension))
        {
            return ExtractionResult.Failed($"No text extractor for '.{extension}' files.");
        }

        // Read the raw bytes so we can sniff for binary content while decoding.
        // The caller owns the stream; we read it but do not dispose it.
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        if (!TextContent.TryDecode(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), out var text))
        {
            return ExtractionResult.Failed("not a text file");
        }

        // A single page, no locator. Whitespace-only -> no usable text (HasText == false),
        // which the pipeline turns into status='failed', error='no extractable text'.
        return ExtractionResult.FromPages([new ExtractedPage { Text = text }]);
    }
}
