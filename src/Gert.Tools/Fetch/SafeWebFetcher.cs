using Gert.Model;
using Gert.Model.Tools;
using Gert.Service.External;

namespace Gert.Tools.Fetch;

/// <summary>
/// The <see cref="IWebFetcher"/> adapter for the <c>web_fetch</c> tool: wraps the
/// singleton <see cref="SafeHttpFetcher"/> (the F5 enforcement point web search's
/// page pulls already use) and maps its exceptions to the port's never-throws
/// contract so policy refusals and failures surface as readable tool errors, never
/// server errors. The caller's own cancellation still throws (the turn owns that
/// signal). The size/time/redirect caps are the fetcher's <see cref="SearXngOptions"/>
/// knobs (<c>Gert:Search</c>), shared with the search summarize step on purpose
/// rather than growing a parallel set.
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
            // TaskCanceledException that is NOT the caller's token - map it.
            return new WebFetchResult { Success = false, Error = "fetch timed out" };
        }
    }
}
