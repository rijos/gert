using Gert.Service.External;

namespace Gert.External.Search;

/// <summary>
/// The <see cref="IWebFetcher"/> adapter for the <c>web_fetch</c> tool: a thin
/// wrapper over the singleton <see cref="SafeHttpFetcher"/> (the F5 enforcement
/// point web search's page pulls already use), mapping its exceptions to the
/// port's never-throws contract so the tool can surface them as readable tool
/// errors:
/// <list type="bullet">
///   <item><see cref="SsrfBlockedException"/> → "URL blocked by fetch policy" —
///   the policy refusal stays card-visible, never a server error.</item>
///   <item><see cref="HttpRequestException"/> (incl. non-2xx via
///   <c>EnsureSuccessStatusCode</c>) → "fetch failed (…)".</item>
///   <item>The fetcher's own wall-clock cap → "fetch timed out"; the CALLER's
///   cancellation still throws (the turn owns that signal).</item>
/// </list>
/// The size/time/redirect caps are the fetcher's own <see cref="SearXngOptions"/>
/// knobs (<c>Gert:Search</c>) — web_fetch deliberately shares them with the
/// search summarize step rather than growing a parallel set.
/// </summary>
public sealed class SafeWebFetcher : IWebFetcher
{
    private readonly SafeHttpFetcher _fetcher;

    public SafeWebFetcher(SafeHttpFetcher fetcher)
    {
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
    }

    /// <inheritdoc />
    public async Task<WebFetchResult> FetchAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        try
        {
            var body = await _fetcher.FetchAsync(url, cancellationToken).ConfigureAwait(false);
            return new WebFetchResult { Success = true, Content = body };
        }
        catch (SsrfBlockedException)
        {
            return new WebFetchResult { Success = false, Error = "URL blocked by fetch policy" };
        }
        catch (HttpRequestException ex)
        {
            var detail = ex.StatusCode is { } status ? $"{(int)status}" : ex.Message;
            return new WebFetchResult { Success = false, Error = $"fetch failed ({detail})" };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient.Timeout (the fetcher's FetchTimeoutSeconds) trips as a
            // TaskCanceledException that is NOT the caller's token — map it.
            return new WebFetchResult { Success = false, Error = "fetch timed out" };
        }
    }
}
