namespace Gert.Service.Ingestion;

/// <summary>
/// Splits extracted pages into overlapping token windows (ingestion step 3,
/// chat-and-tools.md § ingestion). Tokens are whitespace-split words (a stable
/// approximation; see <see cref="ChunkingOptions"/>). Each page is windowed
/// independently so a chunk never straddles a page boundary and keeps that page's
/// locator for citations; ordinals run continuously across the whole document.
/// </summary>
public static class TextChunker
{
    /// <summary>
    /// Window <paramref name="pages"/> into chunks per <paramref name="options"/>.
    /// Whitespace-only pages contribute nothing. Returns an empty list when there is
    /// no usable text (the pipeline then marks the doc <c>failed</c>).
    /// </summary>
    public static IReadOnlyList<TextChunk> Chunk(
        IReadOnlyList<ExtractedPage> pages,
        ChunkingOptions options)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(options);

        var max = Math.Max(1, options.MaxTokens);
        var overlap = Math.Clamp(options.OverlapTokens, 0, max - 1);
        var step = max - overlap; // strictly positive: overlap < max

        var chunks = new List<TextChunk>();
        var ordinal = 0;

        foreach (var page in pages)
        {
            var words = Tokenize(page.Text);
            if (words.Length == 0)
            {
                continue;
            }

            for (var start = 0; start < words.Length; start += step)
            {
                var count = Math.Min(max, words.Length - start);
                var content = string.Join(' ', words, start, count);

                chunks.Add(new TextChunk
                {
                    Ordinal = ordinal++,
                    Content = content,
                    TokenCount = count,
                    Locator = page.Locator,
                });

                // The final window reaches the end; stop so a short tail isn't re-emitted.
                if (start + count >= words.Length)
                {
                    break;
                }
            }
        }

        return chunks;
    }

    /// <summary>Whitespace-split words — the token approximation (no empty entries).</summary>
    private static string[] Tokenize(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
}
