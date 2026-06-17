using System.Text.Json;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Service.External;
using Gert.Service.Tools;

namespace Gert.Tools.Builtin;

/// <summary>
/// The web-fetch tool (chat-and-tools.md section web fetch). Model function
/// <c>web_fetch</c>: pull ONE model-named URL through the <see cref="IWebFetcher"/>
/// port - behind it sits the same SSRF-guarded fetcher web search's page pulls
/// use (security F5), so the egress hardening is the adapter's job, exactly like
/// <see cref="WebSearchTool"/>. A policy block or HTTP failure comes back as a
/// TOOL ERROR the model reads and reacts to (card-visible, like a sandbox
/// error), never a turn fault. An HTML body is reduced to LLM-friendly plain
/// text first (<see cref="HtmlTextExtractor"/> - markup boilerplate would
/// otherwise eat the whole clip); non-HTML (JSON, plain text) passes through
/// raw. Clipping applies AFTER extraction, so the clip spends on content.
/// </summary>
public sealed class WebFetchTool : ITool
{
    /// <summary>Default clip applied to the fetched body (characters).</summary>
    public const int DefaultMaxChars = 8_000;

    /// <summary>Hard ceiling on <c>max_chars</c> - larger asks are clamped, not errored.</summary>
    public const int MaxCharsCeiling = 20_000;

    private readonly IWebFetcher _fetcher;

    public WebFetchTool(IWebFetcher fetcher)
    {
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
    }

    /// <inheritdoc />
    public string Id => "fetch";

    /// <inheritdoc />
    public string Name => "web_fetch";

    /// <inheritdoc />
    public string Description =>
        "Fetch one public web page by URL and return its readable text - use it "
        + "to read a page a search result or the user pointed at. Private, "
        + "internal, and non-http(s) addresses are refused.";

    /// <inheritdoc />
    public string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "url": { "type": "string", "description": "The absolute http(s) URL to fetch." },
            "max_chars": { "type": "integer",
                           "description": "Optional cap on the returned content (default 8000, max 20000)." }
          },
          "required": ["url"]
        }
        """;

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        string? url;
        var maxChars = DefaultMaxChars;
        try
        {
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new ToolResult { Success = false, Error = "invalid arguments: not a JSON object" };
            }

            url = doc.RootElement.TryGetProperty("url", out var u) ? u.GetString() : null;

            if (doc.RootElement.TryGetProperty("max_chars", out var m))
            {
                if (m.ValueKind != JsonValueKind.Number || !m.TryGetInt32(out var requested))
                {
                    return new ToolResult { Success = false, Error = "max_chars must be an integer" };
                }

                // Clamp, don't error: the byte-level cap stays the fetcher's
                // MaxFetchBytes; this only bounds what re-enters the prompt.
                maxChars = Math.Clamp(requested, 1, MaxCharsCeiling);
            }
        }
        catch (JsonException ex)
        {
            return new ToolResult { Success = false, Error = $"invalid arguments: {ex.Message}" };
        }

        // Friendlier pre-check only - the fetcher re-vets authoritatively
        // (scheme, private ranges, every redirect hop, connect-time DNS pin).
        if (string.IsNullOrWhiteSpace(url))
        {
            return new ToolResult { Success = false, Error = "the 'url' argument is required" };
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return new ToolResult { Success = false, Error = "url must be an absolute http(s) URL" };
        }

        var fetched = await _fetcher.FetchAsync(url, cancellationToken).ConfigureAwait(false);

        if (!fetched.Success)
        {
            // SSRF block / HTTP failure: a tool error the model can read and
            // react to - card-visible, the turn continues (security F5).
            return new ToolResult { Success = false, Error = fetched.Error ?? "fetch failed" };
        }

        var content = fetched.Content ?? string.Empty;
        var extracted = HtmlTextExtractor.LooksLikeHtml(content);
        if (extracted)
        {
            content = HtmlTextExtractor.ToPlainText(content);
        }

        var truncated = content.Length > maxChars;
        if (truncated)
        {
            content = content[..maxChars];
        }

        var resultJson = JsonSerializer.Serialize(new
        {
            url,
            content,
            extracted,
            truncated,
            chars = content.Length,
        });

        return new ToolResult
        {
            Success = true,
            ResultJson = resultJson,
            Stdout = $"fetched {url} ({content.Length} chars{(truncated ? ", truncated" : string.Empty)})",
            // One web citation for the fetched page (mirrors web search's
            // citation seeding); bound to the assistant message by TurnRunner.
            Citations =
            [
                new Citation
                {
                    Id = Guid.NewGuid().ToString("D"),
                    MessageId = string.Empty,
                    Ordinal = 1,
                    SourceType = CitationSourceType.Web,
                    DocId = null,
                    Label = url,
                    Locator = url,
                },
            ],
        };
    }
}
