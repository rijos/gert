namespace Gert.External.Search;

/// <summary>
/// Options for the SearXNG web-search adapter + its SSRF-guarded fetch (security F5).
/// Bound from configuration section <c>Gert:Search</c>. The base URL is a non-secret
/// default (appsettings); any auth header, if used, is a secret from env/user-secrets
/// (F8).
/// </summary>
public sealed class SearXngOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Gert:Search";

    /// <summary>Base URL of the SearXNG instance, e.g. <c>http://searxng:8080</c>.</summary>
    public string BaseUrl { get; set; } = "http://localhost:8080";

    /// <summary>Whether to fetch + summarize result pages (the SSRF-exposed step).</summary>
    public bool FetchPages { get; set; }

    /// <summary>Max result pages to fetch when <see cref="FetchPages"/> is on.</summary>
    public int MaxFetch { get; set; } = 3;

    /// <summary>Hard cap on a fetched page's body size (bytes).</summary>
    public int MaxFetchBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>Wall-clock cap for a single page fetch (seconds).</summary>
    public int FetchTimeoutSeconds { get; set; } = 10;

    /// <summary>Max redirects to follow on a fetch (each re-vetted by the SSRF guard).</summary>
    public int MaxRedirects { get; set; } = 3;

    /// <summary>Per-request timeout for the SearXNG search API call (seconds).</summary>
    public int SearchTimeoutSeconds { get; set; } = 15;
}
