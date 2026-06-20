using Gert.Model;
using Gert.Model.Tools;
using Gert.Tools;
using Gert.Tools.Fetch;
using Gert.Tools.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gert.Tools.Search.SearXNG;

/// <summary>
/// Real <see cref="IWebSearch"/> over the SearXNG JSON API (chat-and-tools.md section web
/// search). When <see cref="SearXngOptions.FetchPages"/> is on it pulls a few result pages
/// through the SSRF-guarded <see cref="SafeHttpFetcher"/> (security F5) to fill snippets; a
/// blocked fetch drops that result's snippet but never fails the search.
///
/// <para>
/// <b>Integration-only:</b> the live call + real page fetch need a running instance and
/// network. Unit tests cover the response parsing and the SSRF guard directly.
/// </para>
/// </summary>
public sealed class SearXngWebSearch : IWebSearch
{
    public const string HttpClientName = "searxng";

    private readonly HttpClient _http;
    private readonly SearXngOptions _options;
    private readonly SafeHttpFetcher _fetcher;
    private readonly ILogger<SearXngWebSearch> _logger;

    public SearXngWebSearch(
        HttpClient http,
        IOptions<SearXngOptions> options,
        SafeHttpFetcher fetcher,
        ILogger<SearXngWebSearch> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (maxResults <= 0)
        {
            return [];
        }

        var path = $"/search?format=json&q={Uri.EscapeDataString(query)}";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var results = SearXngResponseParser.Parse(json, maxResults);

        if (!_options.FetchPages)
        {
            return results;
        }

        // Fetch a few result pages through the SSRF guard to enrich snippets;
        // a blocked/failed fetch leaves the original snippet untouched.
        var enriched = new List<WebSearchResult>(results.Count);
        var fetched = 0;
        foreach (var result in results)
        {
            if (fetched >= _options.MaxFetch)
            {
                enriched.Add(result);
                continue;
            }

            fetched++;
            try
            {
                var body = await _fetcher.FetchAsync(result.Url, cancellationToken).ConfigureAwait(false);
                enriched.Add(result with { Snippet = Summarize(body) ?? result.Snippet });
            }
            catch (SsrfBlockedException ex)
            {
                _logger.LogWarning(ex, "Dropping snippet for {Url}: blocked by SSRF guard.", result.Url);
                enriched.Add(result);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Dropping snippet for {Url}: fetch failed.", result.Url);
                enriched.Add(result);
            }
        }

        return enriched;
    }

    /// <summary>
    /// Placeholder summarizer - clips the fetched body until a real model/heuristic step
    /// is wired. The security-relevant fact is that these bytes already passed the SSRF
    /// guard before reaching here.
    /// </summary>
    private static string? Summarize(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var trimmed = body.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed[..500];
    }
}
