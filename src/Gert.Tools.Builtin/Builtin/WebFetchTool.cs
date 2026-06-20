using Gert.Model;
using Gert.Model.Chat;
using Gert.Tools;
using Gert.Tools.Args;
using Gert.Tools.Hosting;
using Gert.Tools.Ports;
using Gert.Tools.Results;
using Gert.Validation;

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
public sealed class WebFetchTool : ToolCall<WebFetchArgs, WebFetchResultPayload>
{
    /// <summary>Default clip applied to the fetched body (characters).</summary>
    public const int DefaultMaxChars = 8_000;

    /// <summary>Hard ceiling on <c>max_chars</c> - larger asks are clamped, not errored.</summary>
    public const int MaxCharsCeiling = 20_000;

    private readonly IWebFetcher _fetcher;

    public WebFetchTool(IValidationProvider validation, IWebFetcher fetcher)
        : base(validation)
    {
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
    }

    /// <inheritdoc />
    public override string Id => "fetch";

    /// <inheritdoc />
    public override string Name => "web_fetch";

    /// <inheritdoc />
    public override string Title => "Fetch pages";

    /// <inheritdoc />
    public override string Icon => "globe";

    /// <inheritdoc />
    public override string Group => "standard";

    /// <inheritdoc />
    public override string Description =>
        "Fetch one public web page by URL and return its readable text - use it "
        + "to read a page a search result or the user pointed at. Private, "
        + "internal, and non-http(s) addresses are refused.";

    /// <inheritdoc />
    public override async Task<ToolCallResult<WebFetchResultPayload>> CallAsync(
        WebFetchArgs args,
        ToolInvocation invocation,
        IToolHost host,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(invocation);

        var url = args.Url;

        // Clamp, don't error: the byte-level cap stays the fetcher's MaxFetchBytes;
        // this only bounds what re-enters the prompt. The validator already floored a
        // supplied value at 1, so the floor here is belt-and-braces.
        var maxChars = args.MaxChars is { } requested
            ? Math.Clamp(requested, 1, MaxCharsCeiling)
            : DefaultMaxChars;

        var fetched = await _fetcher.FetchAsync(url, cancellationToken).ConfigureAwait(false);

        if (!fetched.Success)
        {
            // SSRF block / HTTP failure: a tool error the model can read and
            // react to - card-visible, the turn continues (security F5).
            return ToolCallResult<WebFetchResultPayload>.Fail(fetched.Error ?? "fetch failed");
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

        var payload = new WebFetchResultPayload
        {
            Url = url,
            Content = content,
            Extracted = extracted,
            Truncated = truncated,
            Chars = content.Length,
        };

        return ToolCallResult<WebFetchResultPayload>.Ok(
            payload,
            stdout: $"fetched {url} ({content.Length} chars{(truncated ? ", truncated" : string.Empty)})",
            // One web citation for the fetched page (mirrors web search's citation
            // seeding); bound to the assistant message by TurnRunner.
            citations:
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
            ]);
    }
}
