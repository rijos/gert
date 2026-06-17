using Gert.Model;
using Gert.Model.Tools;

namespace Gert.Service.External;

/// <summary>
/// Port for fetching one model-named URL (chat-and-tools.md section web fetch). The
/// real adapter (<c>Gert.Tools</c>'s <c>SafeWebFetcher</c>) wraps the same
/// SSRF-guarded fetcher the web-search summarize step uses (security F5);
/// tests use a fake. Reached only server-side, never proxied to the browser.
/// </summary>
public interface IWebFetcher
{
    /// <summary>
    /// Fetch a URL through the host's hardened fetcher. Never throws for a
    /// policy-blocked or failed fetch - the result carries the error, so the
    /// tool can surface it to the model as a readable tool error. Cancellation
    /// of <paramref name="cancellationToken"/> still throws
    /// <see cref="OperationCanceledException"/> (the turn owns that signal).
    /// </summary>
    Task<WebFetchResult> FetchAsync(string url, CancellationToken cancellationToken = default);
}
