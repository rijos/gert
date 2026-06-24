using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace Gert.Tools.Builtin.Search.SearXNG;

/// <summary>
/// DI registration for the <c>SearXNG</c> web-search IMPLEMENTATION plugin (tech-stack.md
/// section Architecture). The composition root calls <c>AddGertTools</c> (the generic search
/// selector + the SSRF-guarded fetch over the shared <see cref="SearXngOptions"/> caps) and then
/// this method to make the SearXNG plugin available; configuration selects it via
/// <c>Gert:Tools:Search:Type = SearXNG</c>. This registers the named SearXNG <c>HttpClient</c>
/// (its resilience bound from <see cref="SearXngOptions"/>) and the keyed
/// <see cref="SearXngWebSearchBuilder"/>; the generic <see cref="WebSearchFactory"/> dispatches
/// to it by Type with no central switch.
///
/// <para>
/// <b>Secrets (F8):</b> the base URL is a non-secret <c>appsettings</c> default; any auth header
/// arrives via environment variables / <c>dotnet user-secrets</c>.
/// </para>
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Register the SearXNG search plugin: named HttpClient + the keyed builder.</summary>
    public static IServiceCollection AddGertSearchSearXNG(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The SearXNG typed client (SearXngOptions is bound by the generic AddGertTools, since
        // the same caps also bound the web_fetch fetcher).
        var searxng = services.AddHttpClient(SearXngWebSearch.HttpClientName);
        searxng.AddStandardResilienceHandler()
            .Configure((options, sp) =>
            {
                var opt = sp.GetRequiredService<IOptions<SearXngOptions>>().Value;
                var total = TimeSpan.FromSeconds(opt.SearchTimeoutSeconds);
                // SearchTimeoutSeconds keeps its documented meaning: the whole-call
                // budget, retries included (search GETs are idempotent, so the stock
                // retry policy is safe - dotnet-style-guide.md section 9). Keep the stock
                // 10 s per-attempt timeout unless it would exceed that total.
                options.TotalRequestTimeout.Timeout = total;
                if (options.AttemptTimeout.Timeout > total)
                {
                    options.AttemptTimeout.Timeout = total;
                }
            });
        // After the handler so the explicit backstop survives its InfiniteTimeSpan pin
        // (see the chat client note in Gert.Chat).
        searxng.ConfigureHttpClient((sp, client) =>
        {
            var opt = sp.GetRequiredService<IOptions<SearXngOptions>>().Value;
            client.BaseAddress = new Uri(opt.Parameters.BaseUrl, UriKind.Absolute);
            // Backstop 1 s OUTSIDE the Polly total above, so the pipeline's
            // timeouts - not this CTS - decide outcomes.
            client.Timeout = TimeSpan.FromSeconds(opt.SearchTimeoutSeconds + 1);
            // Cap the buffered search-API body so a large/slow/compromised SearXNG (or a MITM on a
            // plain-http base URL) cannot OOM the host: capped external reads are the standard for
            // every external body (dotnet-style-guide.md section 9; mirrors SafeHttpFetcher /
            // MontySandbox / IsolatedTextExtractor). Reuse MaxFetchBytes (2 MiB) - search-result
            // JSON is metadata, far smaller; over-cap reads throw rather than balloon memory.
            client.MaxResponseContentBufferSize = opt.MaxFetchBytes;
        });

        // Self-register the keyed plugin; the generic WebSearchFactory dispatches by Type.
        services.AddKeyedSingleton<IWebSearchBuilder, SearXngWebSearchBuilder>(
            WebSearchFactory.NormalizeType("SearXNG"));

        return services;
    }
}
