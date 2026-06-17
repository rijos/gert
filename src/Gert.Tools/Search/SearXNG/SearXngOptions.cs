namespace Gert.Tools.Search.SearXNG;

/// <summary>
/// The web-search tool backend (<c>Gert:Tools:Search</c>): pick an implementation via
/// <see cref="Type"/>, configure its connection under <see cref="Parameters"/> - the uniform
/// "functionality -> Type -> Parameters" shape (configuration.md section 4). Only
/// <c>SearXNG</c> ships today. The cross-implementation knobs that also bound the
/// <c>web_fetch</c> tool's SSRF-guarded fetch (security F5) sit beside <see cref="Type"/>;
/// only the connection (<see cref="SearXngParameters.BaseUrl"/>) lives under
/// <see cref="Parameters"/>. The base URL is a non-secret default (appsettings); any auth
/// header, if used, is a secret from env/user-secrets (F8).
/// </summary>
public sealed class SearXngOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Gert:Tools:Search";

    /// <summary>The search implementation to use. <c>SearXNG</c> today.</summary>
    public string Type { get; set; } = "SearXNG";

    /// <summary>Whether to fetch + summarize result pages (the SSRF-exposed step).</summary>
    public bool FetchPages { get; set; }

    /// <summary>Max result pages to fetch when <see cref="FetchPages"/> is on.</summary>
    public int MaxFetch { get; set; } = 3;

    /// <summary>Hard cap on a fetched page's body size (bytes). Also bounds <c>web_fetch</c>.</summary>
    public int MaxFetchBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>Wall-clock cap for a single page fetch (seconds). Also bounds <c>web_fetch</c>.</summary>
    public int FetchTimeoutSeconds { get; set; } = 10;

    /// <summary>Max redirects to follow on a fetch (each re-vetted by the SSRF guard).</summary>
    public int MaxRedirects { get; set; } = 3;

    /// <summary>Per-request timeout for the SearXNG search API call (seconds).</summary>
    public int SearchTimeoutSeconds { get; set; } = 15;

    /// <summary>The implementation's connection (what changes when <see cref="Type"/> changes).</summary>
    public SearXngParameters Parameters { get; set; } = new();
}
