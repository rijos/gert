using System.Text;

namespace Gert.Service.Ingestion;

/// <summary>
/// The md/txt <see cref="ITextExtractor"/> - reads the upload stream as UTF-8 text
/// (chat-and-tools.md section ingestion: "md/txt -> read"). One <see cref="ExtractedPage"/>
/// with a null locator is produced; an empty/whitespace-only file yields no usable
/// text, so the pipeline marks the document <c>failed</c> ("no extractable text").
///
/// <para>
/// pdf/docx are <b>not</b> handled here - <see cref="CanExtract"/> returns false for
/// them and the hardened isolated-subprocess extractor (security F7) lands in
/// <c>Gert.External</c>. Without it the composite extractor falls through to a
/// "no extractor" failure for those types.
/// </para>
/// </summary>
public sealed class PlainTextExtractor : ITextExtractor
{
    private static readonly IReadOnlySet<string> Handled =
        new HashSet<string>(StringComparer.Ordinal) { "md", "txt" };

    /// <inheritdoc />
    public bool CanExtract(string extension) =>
        extension is not null && Handled.Contains(extension);

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

        // Read as UTF-8, honouring a BOM. Leave the stream open - the caller owns it.
        using var reader = new StreamReader(
            content,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: true);

        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        // A single page, no locator. Whitespace-only -> no usable text (HasText == false),
        // which the pipeline turns into status='failed', error='no extractable text'.
        return ExtractionResult.FromPages([new ExtractedPage { Text = text }]);
    }
}
