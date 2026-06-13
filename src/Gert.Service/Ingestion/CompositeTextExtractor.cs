namespace Gert.Service.Ingestion;

/// <summary>
/// The default <see cref="ITextExtractor"/> the pipeline depends on: it routes a
/// blob to the first registered extractor that <see cref="ITextExtractor.CanExtract"/>s
/// its type. Today only <see cref="PlainTextExtractor"/> (md/txt) is wired; an
/// unhandled type (security F7)
/// returns <see cref="ExtractionResult.Failed"/> so the pipeline marks the document
/// <c>failed</c> ("extractor not available") rather than throwing.
///
/// <para>
/// Gert.External swaps in the hardened pdf/docx extractor by registering it as another
/// <see cref="ITextExtractor"/> - no change here or in the pipeline.
/// </para>
/// </summary>
public sealed class CompositeTextExtractor : ITextExtractor
{
    private readonly IReadOnlyList<ITextExtractor> _extractors;

    /// <summary>
    /// Compose the available per-type extractors. The composite itself is excluded
    /// (filtered by reference) so DI can register it alongside the leaf extractors
    /// without self-recursion.
    /// </summary>
    public CompositeTextExtractor(IEnumerable<ITextExtractor> extractors)
    {
        ArgumentNullException.ThrowIfNull(extractors);
        _extractors = extractors.Where(e => e is not CompositeTextExtractor).ToList();
    }

    /// <inheritdoc />
    public bool CanExtract(string extension) =>
        _extractors.Any(e => e.CanExtract(extension));

    /// <inheritdoc />
    public Task<ExtractionResult> ExtractAsync(
        Stream content,
        string extension,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        foreach (var extractor in _extractors)
        {
            if (extractor.CanExtract(extension))
            {
                return extractor.ExtractAsync(content, extension, cancellationToken);
            }
        }

        // No extractor available for this type.
        return Task.FromResult(
            ExtractionResult.Failed($"extractor not available for '.{extension}'"));
    }
}
