using Gert.Model;
using Gert.Model.Chat;
using Gert.Model.Tools;
using Gert.Tools;
using Gert.Validation;

namespace Gert.Tools.Builtin;

/// <summary>
/// The web-search tool (chat-and-tools.md section web search). Model function
/// <c>web_search</c>: forwards the query to <see cref="IWebSearch"/> and shapes the
/// results into a <see cref="WebSearchToolResult"/> - a JSON payload for the model plus
/// web-type <see cref="Citation"/>s. The tool only calls the port; SSRF/egress hardening
/// is the adapter's job (security F5).
/// </summary>
public sealed class WebSearchTool : ToolCall<WebSearchArgs, WebSearchToolResult>
{
    /// <summary>How many results to ask the port for.</summary>
    private const int MaxResults = 5;

    private readonly IWebSearch _search;

    public WebSearchTool(IValidationProvider validation, IWebSearch search)
        : base(validation)
    {
        _search = search ?? throw new ArgumentNullException(nameof(search));
    }

    /// <inheritdoc />
    public override string Id => "search";

    /// <inheritdoc />
    public override string Name => "web_search";

    /// <inheritdoc />
    public override string Title => "Search";

    /// <inheritdoc />
    public override string Icon => "globe";

    /// <inheritdoc />
    public override string Group => "standard";

    /// <inheritdoc />
    public override string Description =>
        "Search the public web; returns the most relevant results with their "
        + "titles and URLs.";

    /// <inheritdoc />
    public override string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "description": "The web search query." }
          },
          "required": ["query"]
        }
        """;

    /// <inheritdoc />
    public override async Task<ToolCallResult<WebSearchToolResult>> CallAsync(
        WebSearchArgs args,
        ToolInvocation invocation,
        IToolHost host,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(invocation);

        var results = await _search.SearchAsync(args.Query, MaxResults, cancellationToken).ConfigureAwait(false);

        return Shape(results);
    }

    /// <summary>Turn the web results into the model-facing JSON plus web citations.</summary>
    private static ToolCallResult<WebSearchToolResult> Shape(IReadOnlyList<WebSearchResult> results)
    {
        var citations = new List<Citation>(results.Count);
        var resultRows = new List<WebSearchHit>(results.Count);

        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            var ordinal = i + 1;

            citations.Add(new Citation
            {
                Id = Guid.NewGuid().ToString("D"),
                MessageId = string.Empty, // bound to the assistant message by TurnRunner.
                Ordinal = ordinal,
                SourceType = CitationSourceType.Web,
                DocId = null,
                Label = r.Title,
                Locator = r.Url,
            });

            resultRows.Add(new WebSearchHit
            {
                Ordinal = ordinal,
                Title = r.Title,
                Url = r.Url,
                Snippet = r.Snippet,
            });
        }

        return ToolCallResult<WebSearchToolResult>.Ok(
            new WebSearchToolResult { Results = resultRows }, citations: citations);
    }
}
