namespace Gert.Tools;

/// <summary>Arguments for the web-search tool (<c>web_search</c>): the public-web query.</summary>
public sealed record WebSearchArgs
{
    /// <summary>The web search query (required).</summary>
    public string Query { get; init; } = string.Empty;
}
