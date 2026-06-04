using System.Net.Http.Headers;
using Gert.External.Isolation;
using Gert.External.Sandbox;
using Gert.External.Search;
using Gert.External.Vllm;
using Gert.Service.External;
using Gert.Service.Ingestion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gert.External;

/// <summary>
/// One-call DI registration for the real outside-world adapters (tech-stack.md
/// § Architecture: <c>AddGertExternal(cfg)</c>). Both hosts (Api, and Console once it
/// lands in U11) call this in place of the test fakes; the service layer keeps talking
/// only to the ports (<see cref="IChatModelClient"/>, <see cref="IEmbeddingClient"/>,
/// <see cref="IWebSearch"/>, <see cref="ISandbox"/>, <see cref="ITextExtractor"/>).
///
/// <para>
/// <b>Secrets (F8):</b> options bind from configuration sections; real secret values
/// (the vLLM bearer key) arrive via environment variables / <c>dotnet user-secrets</c> /
/// a secret store — <c>appsettings.json</c> carries only non-secret defaults. The vLLM
/// <c>Authorization</c> header is set from the bound (resolved) option at client-config
/// time, so it picks up whichever provider supplied it.
/// </para>
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Register all real <c>Gert.External</c> adapters + HttpClients + options.</summary>
    public static IServiceCollection AddGertExternal(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        AddOptions(services, configuration);
        AddVllm(services);
        AddSearch(services);
        AddSandbox(services);
        AddIsolatedExtractor(services);

        return services;
    }

    private static void AddOptions(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<VllmOptions>()
            .Bind(configuration.GetSection(VllmOptions.SectionName))
            .ValidateOnStart();
        services.AddOptions<SearXngOptions>()
            .Bind(configuration.GetSection(SearXngOptions.SectionName))
            .ValidateOnStart();
        services.AddOptions<SandboxOptions>()
            .Bind(configuration.GetSection(SandboxOptions.SectionName))
            .ValidateOnStart();
        services.AddOptions<ExtractorOptions>()
            .Bind(configuration.GetSection(ExtractorOptions.SectionName))
            .ValidateOnStart();
    }

    private static void AddVllm(IServiceCollection services)
    {
        // One typed client for both chat + embeddings (same upstream). Base URL +
        // secret bearer key from the bound options (F8). Polly standard pipeline adds
        // a per-attempt timeout + retry around the call (tech-stack § HTTP).
        services.AddHttpClient(VllmChatModelClient.HttpClientName)
            .ConfigureHttpClient((sp, client) =>
            {
                var opt = sp.GetRequiredService<IOptions<VllmOptions>>().Value;
                client.BaseAddress = new Uri(opt.BaseUrl, UriKind.Absolute);
                client.Timeout = TimeSpan.FromSeconds(opt.RequestTimeoutSeconds);
                if (!string.IsNullOrEmpty(opt.ApiKey))
                {
                    client.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", opt.ApiKey);
                }
            })
            .AddStandardResilienceHandler();

        services.AddSingleton<IChatModelClient>(sp => new VllmChatModelClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(VllmChatModelClient.HttpClientName),
            sp.GetRequiredService<IOptions<VllmOptions>>(),
            sp.GetRequiredService<ILogger<VllmChatModelClient>>()));

        services.AddSingleton<IEmbeddingClient>(sp => new VllmEmbeddingClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(VllmEmbeddingClient.HttpClientName),
            sp.GetRequiredService<IOptions<VllmOptions>>(),
            sp.GetRequiredService<ILogger<VllmEmbeddingClient>>()));
    }

    private static void AddSearch(IServiceCollection services)
    {
        services.AddHttpClient(SearXngWebSearch.HttpClientName)
            .ConfigureHttpClient((sp, client) =>
            {
                var opt = sp.GetRequiredService<IOptions<SearXngOptions>>().Value;
                client.BaseAddress = new Uri(opt.BaseUrl, UriKind.Absolute);
                client.Timeout = TimeSpan.FromSeconds(opt.SearchTimeoutSeconds);
            })
            .AddStandardResilienceHandler();

        // The SSRF-guarded fetcher owns its own SocketsHttpHandler (ConnectCallback is
        // the enforcement point), so it is NOT an IHttpClientFactory client — it must
        // not share a handler whose connect path we don't control.
        services.AddSingleton<SafeHttpFetcher>();

        services.AddSingleton<IWebSearch>(sp => new SearXngWebSearch(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(SearXngWebSearch.HttpClientName),
            sp.GetRequiredService<IOptions<SearXngOptions>>(),
            sp.GetRequiredService<SafeHttpFetcher>(),
            sp.GetRequiredService<ILogger<SearXngWebSearch>>()));
    }

    private static void AddSandbox(IServiceCollection services)
    {
        services.AddSingleton<ISandbox, GVisorSandbox>();
    }

    private static void AddIsolatedExtractor(IServiceCollection services)
    {
        // Register the pdf/docx leaf under the SAME key the CompositeTextExtractor
        // enumerates (Gert.Service.ServiceCollectionExtensions.LeafExtractorKey). No
        // change to the composite or the pipeline — the composite now routes pdf/docx
        // here instead of "not available".
        services.AddKeyedSingleton<ITextExtractor, IsolatedTextExtractor>(
            Gert.Service.ServiceCollectionExtensions.LeafExtractorKey);
    }
}
