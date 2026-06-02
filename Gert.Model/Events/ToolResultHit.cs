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
}
