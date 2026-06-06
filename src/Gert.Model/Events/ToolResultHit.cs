using Gert.Model.Chat;

namespace Gert.Model.Events;

/// <summary>
/// A single hit in a <see cref="ToolResultEvent"/> — e.g. the
/// <c>{"doc","page","score"}</c> rows the RAG card renders (rest-api.md SSE table).
/// </summary>
public sealed record ToolResultHit
{
    public string? Doc { get; init; }

    public string? Page { get; init; }

    public double? Score { get; init; }

    /// <summary>Web-result title (for web_search hits).</summary>
    public string? Title { get; init; }

    public string? Url { get; init; }

    /// <summary>
    /// Project citations into card hit rows — web citations become title/url
    /// hits, document citations doc/page hits. The one mapping shared by the
    /// live <c>tool_result</c> event and the thread-reload card reconstruction.
    /// </summary>
    public static IReadOnlyList<ToolResultHit> FromCitations(IReadOnlyList<Citation> citations)
    {
        ArgumentNullException.ThrowIfNull(citations);

        var hits = new List<ToolResultHit>(citations.Count);
        foreach (var citation in citations)
        {
            if (citation.SourceType == CitationSourceType.Web)
            {
                hits.Add(new ToolResultHit
                {
                    Title = citation.Label,
                    Url = citation.Locator,
                    Score = citation.Score,
                });
            }
            else
            {
                hits.Add(new ToolResultHit
                {
                    Doc = citation.Label,
                    Page = citation.Locator,
                    Score = citation.Score,
                });
            }
        }

        return hits;
    }
}
