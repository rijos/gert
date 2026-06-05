namespace Gert.Service.External;

/// <summary>
/// Port for web search (chat-and-tools.md § web search). The real client
/// (SearXNG + the SSRF-guarded fetch, security F5) lives in <c>Gert.External</c>
/// (U10); tests use a fake. Reached only server-side, never proxied to the
/// browser.
/// </summary>
public interface IWebSearch
{
    /// <summary>Search the web and return the results worth keeping.</summary>
    Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken = default);
}
