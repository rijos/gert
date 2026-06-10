using System.Net.Http.Headers;
using Gert.External.Isolation;
using Gert.External.Sandbox;
using Gert.External.Search;
using Gert.External.Vllm;
using Gert.Service.External;
using Gert.Service.Ingestion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

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
        AddSandbox(services, configuration);
        AddIsolatedExtractor(services);

        // The operator model catalog (Gert:Models + vLLM fallback) — feeds both
        // GET /api/models and the TurnPlanner tool-capability gate. Closes over
        // the configuration parameter: not every host (Console) puts
        // IConfiguration in the container.
        services.AddSingleton<IModelCatalog>(sp => new ConfigModelCatalog(
            configuration,
            sp.GetRequiredService<IOptions<VllmOptions>>()));

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
        services.AddOptions<MontyOptions>()
            .Bind(configuration.GetSection(MontyOptions.SectionName))
            .ValidateOnStart();
        services.AddOptions<ExtractorOptions>()
            .Bind(configuration.GetSection(ExtractorOptions.SectionName))
            .ValidateOnStart();
    }

    private static void AddVllm(IServiceCollection services)
    {
        // Two named clients for the one upstream, split because their timeout
        // ownership differs (dotnet-style-guide.md §9). The layering, outermost →
        // innermost, is: HttpClient.Timeout wraps the whole Polly pipeline; the
        // pipeline bounds time-to-headers per attempt and across retries; and —
        // chat only — the SSE stream that follows the headers is owned by the
        // turn-lifetime token TurnRunner passes in (turn-budgets.md §4a,
        // MaxTurnDuration). Base URL + secret bearer key from the bound options (F8).

        // Chat STREAMS (ResponseHeadersRead): a finite HttpClient.Timeout stays
        // linked to the body stream and would kill any generation round longer than
        // it, silently undercutting the turn budget — so it is infinite and the turn
        // budget owns the wall clock. The Polly pipeline completes at the response
        // headers, so it genuinely bounds only the pre-stream phase (connect + model
        // accept) — that's the design.
        // NOTE: AddStandardResilienceHandler registers its own ConfigureHttpClient
        // action that pins HttpClient.Timeout to InfiniteTimeSpan (the pipeline is
        // meant to own timeouts). Configure actions run in registration order and
        // the last write wins — so the explicit Timeout below must be registered
        // AFTER the handler, or it is silently overridden.
        var chat = services.AddHttpClient(VllmChatModelClient.HttpClientName);
        chat.AddStandardResilienceHandler().Configure(ConfigureVllmResilience);
        chat.ConfigureHttpClient((sp, client) =>
        {
            ConfigureVllmClient(sp, client);
            client.Timeout = Timeout.InfiniteTimeSpan;
        });

        // Embeddings BUFFER: same upstream, same options-bound pipeline, but a
        // finite client timeout sits 1 s OUTSIDE the pipeline's total, so it covers
        // the buffered body read while the pipeline's timeouts — not this CTS —
        // decide the pre-stream outcomes.
        var embeddings = services.AddHttpClient(VllmEmbeddingClient.HttpClientName);
        embeddings.AddStandardResilienceHandler().Configure(ConfigureVllmResilience);
        // After the handler for the same registration-order reason as the chat client.
        embeddings.ConfigureHttpClient((sp, client) =>
        {
            ConfigureVllmClient(sp, client);
            var opt = sp.GetRequiredService<IOptions<VllmOptions>>().Value;
            client.Timeout = VllmTotalTimeout(opt) + TimeSpan.FromSeconds(1);
        });

        // Singletons: stateless shells over a factory-created client + bound options.
        services.AddSingleton<IChatModelClient>(sp => new VllmChatModelClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(VllmChatModelClient.HttpClientName),
            sp.GetRequiredService<IOptions<VllmOptions>>(),
            sp.GetRequiredService<ILogger<VllmChatModelClient>>()));

        services.AddSingleton<IEmbeddingClient>(sp => new VllmEmbeddingClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(VllmEmbeddingClient.HttpClientName),
            sp.GetRequiredService<IOptions<VllmOptions>>(),
            sp.GetRequiredService<ILogger<VllmEmbeddingClient>>()));
    }

    /// <summary>Shared vLLM client config: base URL + secret bearer key (F8).</summary>
    private static void ConfigureVllmClient(IServiceProvider sp, HttpClient client)
    {
        var opt = sp.GetRequiredService<IOptions<VllmOptions>>().Value;
        client.BaseAddress = new Uri(opt.BaseUrl, UriKind.Absolute);
        if (!string.IsNullOrEmpty(opt.ApiKey))
        {
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", opt.ApiKey);
        }
    }

    /// <summary>
    /// Configure the standard resilience pipeline FROM the bound <see cref="VllmOptions"/>
    /// (dotnet-style-guide.md §9) — stock defaults would retry on a 10 s time-to-first-byte
    /// (routine GPU queueing) and hard-fail at 30 s, silently undercutting the configured
    /// per-attempt timeout. Shared by chat and embeddings: both POSTs complete the pipeline
    /// at the response headers, so every bound here covers only the pre-stream phase.
    /// </summary>
    private static void ConfigureVllmResilience(HttpStandardResilienceOptions options, IServiceProvider sp)
    {
        var opt = sp.GetRequiredService<IOptions<VllmOptions>>().Value;
        var attempt = TimeSpan.FromSeconds(opt.RequestTimeoutSeconds);

        // Per attempt: how long the model may take to ACCEPT the request (time to
        // response headers). Across attempts: the coherent worst case, see
        // VllmTotalTimeout. The stream after the headers is out of scope here.
        options.AttemptTimeout.Timeout = attempt;
        options.TotalRequestTimeout.Timeout = VllmTotalTimeout(opt);

        // The pipeline's own validator requires SamplingDuration >= 2 x AttemptTimeout;
        // scale it with the attempt instead of inheriting the stock 30 s.
        options.CircuitBreaker.SamplingDuration = attempt + attempt;

        if (opt.RetryCount > 0)
        {
            // Retrying the chat POST here is safe despite non-idempotence: the
            // pipeline completes at the response headers, so a retried attempt
            // means the model never accepted the request and no tokens were
            // streamed. Embedding POSTs are idempotent outright.
            options.Retry.MaxRetryAttempts = opt.RetryCount;
        }
        else
        {
            // RetryCount <= 0 means "no retries"; the pipeline validator forbids
            // MaxRetryAttempts = 0, so disable via the predicate instead.
            options.Retry.ShouldHandle = _ => PredicateResult.False();
        }
    }

    /// <summary>
    /// Worst-case wall clock for the whole vLLM pipeline: (RetryCount + 1) attempts each
    /// running their full <see cref="VllmOptions.RequestTimeoutSeconds"/>, plus slack for
    /// the exponential backoff delays between attempts (2 s base ⇒ 2·(2^RetryCount − 1) s
    /// before jitter; ×1.5 headroom). A pathological jitter draw spilling over merely ends
    /// an already-failing call a moment early.
    /// </summary>
    private static TimeSpan VllmTotalTimeout(VllmOptions opt)
    {
        var attempts = opt.RetryCount > 0 ? opt.RetryCount + 1 : 1;
        var backoffSlackSeconds = opt.RetryCount > 0 ? 3.0 * (Math.Pow(2, opt.RetryCount) - 1) : 0.0;
        return TimeSpan.FromSeconds(((double)opt.RequestTimeoutSeconds * attempts) + backoffSlackSeconds);
    }

    private static void AddSearch(IServiceCollection services)
    {
        var searxng = services.AddHttpClient(SearXngWebSearch.HttpClientName);
        searxng.AddStandardResilienceHandler()
            .Configure((options, sp) =>
            {
                var opt = sp.GetRequiredService<IOptions<SearXngOptions>>().Value;
                var total = TimeSpan.FromSeconds(opt.SearchTimeoutSeconds);
                // SearchTimeoutSeconds keeps its documented meaning: the whole-call
                // budget, retries included (search GETs are idempotent, so the stock
                // retry policy is safe — dotnet-style-guide.md §9). Keep the stock
                // 10 s per-attempt timeout unless it would exceed that total.
                options.TotalRequestTimeout.Timeout = total;
                if (options.AttemptTimeout.Timeout > total)
                {
                    options.AttemptTimeout.Timeout = total;
                }
            });
        // After the handler so the explicit backstop survives its InfiniteTimeSpan pin
        // (see the chat client note above).
        searxng.ConfigureHttpClient((sp, client) =>
        {
            var opt = sp.GetRequiredService<IOptions<SearXngOptions>>().Value;
            client.BaseAddress = new Uri(opt.BaseUrl, UriKind.Absolute);
            // Backstop 1 s OUTSIDE the Polly total above, so the pipeline's
            // timeouts — not this CTS — decide outcomes. (Previously this sat
            // at 15 s INSIDE Polly's stock 30 s total: incoherent layering.)
            client.Timeout = TimeSpan.FromSeconds(opt.SearchTimeoutSeconds + 1);
        });

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

    private static void AddSandbox(IServiceCollection services, IConfiguration configuration)
    {
        // Both backends sit behind the one ISandbox port; the operator picks via
        // Gert:Sandbox:Backend. Default monty — it needs no container infra, so it is the
        // backend we can actually run today (the gVisor bundle writer is still TODO).
        var backend = (configuration[$"{SandboxOptions.SectionName}:Backend"] ?? "monty")
            .Trim().ToLowerInvariant();

        switch (backend)
        {
            case "":
            case "monty":
                AddMonty(services);
                break;
            case "gvisor":
                services.AddSingleton<ISandbox, GVisorSandbox>();
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown {SandboxOptions.SectionName}:Backend '{backend}'. Use 'monty' or 'gvisor'.");
        }
    }

    private static void AddMonty(IServiceCollection services)
    {
        // Startup relation check (registered here so it only binds when the monty
        // backend is selected — gVisor does not use the sidecar): the HTTP backstop
        // must sit above monty's wall clock, or the transport would kill runs the
        // interpreter was about to return cleanly. MontyOptions already has
        // ValidateOnStart, which picks this validator up.
        services.AddSingleton<IValidateOptions<MontyOptions>, MontySandboxTimeoutRelationValidator>();

        // A plain typed client: NO standard resilience handler. A sandbox run is not safely
        // retryable (re-running code wastes a run, and under code-mode would re-invoke
        // tools), so the only time bounds are monty's own wall clock + this HTTP backstop.
        services.AddHttpClient(MontySandbox.HttpClientName)
            .ConfigureHttpClient((sp, client) =>
            {
                var opt = sp.GetRequiredService<IOptions<MontyOptions>>().Value;
                client.BaseAddress = new Uri(opt.BaseUrl, UriKind.Absolute);
                client.Timeout = TimeSpan.FromSeconds(opt.RequestTimeoutSeconds);
            });

        services.AddSingleton<ISandbox>(sp => new MontySandbox(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(MontySandbox.HttpClientName),
            sp.GetRequiredService<IOptions<SandboxOptions>>(),
            sp.GetRequiredService<ILogger<MontySandbox>>()));
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

    /// <summary>
    /// Enforces at startup what <see cref="MontyOptions.RequestTimeoutSeconds"/> documents:
    /// the HTTP backstop sits strictly <b>above</b> <see cref="SandboxOptions.WallClockSeconds"/>,
    /// so monty's own limit trips first and returns a clean timed-out result
    /// (chat-and-tools.md § sandbox).
    /// </summary>
    private sealed class MontySandboxTimeoutRelationValidator : IValidateOptions<MontyOptions>
    {
        private readonly IOptions<SandboxOptions> _sandbox;

        public MontySandboxTimeoutRelationValidator(IOptions<SandboxOptions> sandbox)
        {
            _sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));
        }

        public ValidateOptionsResult Validate(string? name, MontyOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            var wallClock = _sandbox.Value.WallClockSeconds;
            if (options.RequestTimeoutSeconds <= wallClock)
            {
                return ValidateOptionsResult.Fail(
                    $"{MontyOptions.SectionName}:RequestTimeoutSeconds ({options.RequestTimeoutSeconds}s) " +
                    $"must be greater than {SandboxOptions.SectionName}:WallClockSeconds ({wallClock}s): " +
                    "the HTTP timeout is only a backstop for a hung sidecar — monty's own wall clock must " +
                    "trip first so a long run returns a clean timed-out result instead of a transport error. " +
                    "Raise RequestTimeoutSeconds above WallClockSeconds (or lower WallClockSeconds).");
            }

            return ValidateOptionsResult.Success;
        }
    }
}
