using Gert.Tools.Schema;

namespace Gert.Tools.Args;

/// <summary>Arguments for the web-search tool (<c>web_search</c>): the public-web query.</summary>
public sealed record WebSearchArgs
{
    /// <summary>The web search query (required).</summary>
    [ToolParameterDescription("The web search query.")]
    public string Query { get; init; } = string.Empty;
}
