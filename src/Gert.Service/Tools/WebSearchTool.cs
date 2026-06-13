using System.Text.Json;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Service.External;

namespace Gert.Service.Tools;

/// <summary>
/// The web-search tool (chat-and-tools.md section web search). Model function
/// <c>web_search</c>: forwards the query to <see cref="IWebSearch"/> (SearXNG +
/// the SSRF-guarded fetch lives behind the port, security F5) and shapes
/// the results worth keeping into a <see cref="ToolResult"/> - a JSON payload for
/// the model plus web-type <see cref="Citation"/>s. The tool only calls the port;
/// the egress hardening is the adapter's job.
/// </summary>
public sealed class WebSearchTool : ITool
{
    /// <summary>How many results to ask the port for (the mockup keeps a few).</summary>
    private const int MaxResults = 5;

    private readonly IWebSearch _search;

    public WebSearchTool(IWebSearch search)
    {
        _search = search ?? throw new ArgumentNullException(nameof(search));
    }

    /// <inheritdoc />
    public string Id => "search";

    /// <inheritdoc />
    public string Name => "web_search";

    /// <inheritdoc />
    public string Description =>
        "Search the public web; returns the most relevant results with their "
        + "titles and URLs.";

    /// <inheritdoc />
    public string ParametersSchema =>
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
    public async Task<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        string query;
        try
        {
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            query = doc.RootElement.TryGetProperty("query", out var q) ? q.GetString() ?? string.Empty : string.Empty;
        }
        catch (JsonException ex)
        {
            return new ToolResult { Success = false, Error = $"invalid arguments: {ex.Message}" };
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return new ToolResult { Success = false, Error = "the 'query' argument is required" };
        }

        var results = await _search.SearchAsync(query, MaxResults, cancellationToken).ConfigureAwait(false);

        return Shape(results);
    }

    /// <summary>Turn the web results into the model-facing JSON plus web citations.</summary>
    private static ToolResult Shape(IReadOnlyList<WebSearchResult> results)
    {
        var citations = new List<Citation>(results.Count);
        var resultRows = new List<object>(results.Count);

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

            resultRows.Add(new
            {
                ordinal,
                title = r.Title,
                url = r.Url,
                snippet = r.Snippet,
            });
        }

        var resultJson = JsonSerializer.Serialize(new { results = resultRows });

        return new ToolResult
        {
            Success = true,
            ResultJson = resultJson,
            Citations = citations,
        };
    }
}
