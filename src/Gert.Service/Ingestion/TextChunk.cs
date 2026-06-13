namespace Gert.Service.Ingestion;

/// <summary>
/// A windowed chunk produced by <see cref="TextChunker"/> before embedding - the
/// text, its approximate token count, the source locator carried from the
/// <see cref="ExtractedPage"/>, and its project-wide ordinal. Becomes a
/// <c>chunks</c> row (with <c>page</c> = <see cref="Locator"/>) after embedding.
/// </summary>
public sealed record TextChunk
{
    public required int Ordinal { get; init; }

    public required string Content { get; init; }

    public required int TokenCount { get; init; }

    public string? Locator { get; init; }
}
