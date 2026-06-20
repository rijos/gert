using Gert.Model;
using Gert.Model.Tools;

namespace Gert.Tools.Ports;

/// <summary>
/// Port for web search (chat-and-tools.md section web search). The real client
/// (SearXNG + the SSRF-guarded fetch, security F5) lives in <c>Gert.Tools</c>;
/// tests use a fake. Reached only server-side, never proxied to the browser.
/// </summary>
public interface IWebSearch
{
    /// <summary>Search the web and return the results worth keeping.</summary>
    Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken = default);
}
