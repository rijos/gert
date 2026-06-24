using System.Net.Http.Headers;
using Gert.Chat;
using Gert.Model.Chat;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace Gert.Chat.OpenAI;

/// <summary>
/// DI registration for the <c>OpenAI</c> chat IMPLEMENTATION plugin (tech-stack.md section
/// Architecture). The composition root calls <see cref="AddGertChat"/> (the generic catalog +
/// keyed-plugin factory) and then this method to make the OpenAI plugin available; configuration
/// selects it per provider via <c>Gert:Chat:Providers:&lt;slug&gt;:Type = OpenAI</c>. This method
/// registers the OpenAI chat-client builder (keyed by its Type), the per-provider-slug transports
/// + bound <see cref="ChatProviderParameters"/>, the embeddings client (<c>Gert:Embeddings</c>),
/// and the zero-config <see cref="IDefaultChatProvider"/>.
///
/// <para>
/// <b>Secrets (F8):</b> options bind from configuration sections; real secret values
/// (the vLLM bearer keys) arrive via environment variables / <c>dotnet user-secrets</c> -
/// <c>appsettings.json</c> carries only non-secret defaults.
/// </para>
///
/// <para>
/// <b>Resilience is per item</b> (configuration.md intro; section 3): there is no shared
/// <c>Gert:Http</c> section. Each chat provider's <c>Parameters</c> and the embeddings
/// <c>Parameters</c> carry their own <c>RequestTimeoutSeconds</c>/<c>RetryCount</c>; the
/// <see cref="ConfigureTransportResilience"/>/<see cref="TotalTransportTimeout"/> helpers take
/// those primitives, not an options type.
/// </para>
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Register the OpenAI chat plugin: builder + per-slug transports + embeddings + default.</summary>
    public static IServiceCollection AddGertChatOpenAI(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<EmbeddingsOptions>()
            .Bind(configuration.GetSection(EmbeddingsOptions.SectionName))
            .ValidateOnStart();
        // Fail-closed Type discriminator: an unknown embeddings Type fails fast at startup.
        services.AddSingleton<IValidateOptions<EmbeddingsOptions>, EmbeddingsTypeValidator>();
        // Fail-closed connection params: a typo'd BaseUrl, non-positive Dimensions, or a negative
        // timeout/retry fails at boot with the named knob instead of on the first embedding call.
        services.AddSingleton<IValidateOptions<EmbeddingsOptions>, EmbeddingsParametersValidator>();

        AddChat(services, configuration);
        AddEmbeddings(services);

        // The OpenAI chat-client plugin, keyed by its (normalized) Type, plus the zero-config
        // default provider (synthesized from the embeddings base URL). The generic catalog +
        // factory (AddGertChat) resolve these without referencing this assembly.
        services.AddKeyedSingleton<IChatModelClientBuilder, OpenAIChatModelClientBuilder>(
            ChatClientFactory.NormalizeType("OpenAI"));
        services.AddSingleton<IDefaultChatProvider, OpenAIDefaultChatProvider>();

        return services;
    }

    private static void AddChat(IServiceCollection services, IConfiguration configuration)
    {
        // Debug-only wire trace of the actual request bytes to the upstream (sampling
        // params, tools block, chat_template_kwargs). Transient per the
        // IHttpClientFactory contract; added AFTER the resilience handler so it sits
        // innermost and sees the request as it leaves for the transport. Silent above
        // Debug, key redacted.
        services.AddTransient<OpenAIWireLogger>();

        // Fail-closed per-provider connection params: a typo'd BaseUrl or negative timeout/retry on
        // any slug fails fast at startup (each slug's options carry ValidateOnStart below), naming
        // the offending slug + knob instead of erroring on that provider's first turn.
        services.AddSingleton<IValidateOptions<ChatProviderParameters>, ChatProviderParametersValidator>();

        foreach (var slug in OpenAISlugs(configuration))
        {
            // Each OpenAI provider slug carries its own connection + sampling as NAMED
            // ChatProviderParameters options (keyed by the slug). A configured slug binds from
            // its Parameters section; the synthesized zero-config "default" slug (no config
            // section) is configured from the embeddings base URL instead, so a bare boot still
            // has a real connection.
            var bound = services.AddOptions<ChatProviderParameters>(slug).ValidateOnStart();
            if (slug == ChatProviderInfo.DefaultId &&
                configuration.GetSection($"{ChatProviderOptions.SectionName}:{slug}").Exists() == false)
            {
                bound.Configure<IOptions<EmbeddingsOptions>>((p, emb) =>
                {
                    p.BaseUrl = emb.Value.Parameters.BaseUrl;
                    p.Model = "default";
                });
            }
            else
            {
                bound.Bind(configuration.GetSection($"{ChatProviderOptions.SectionName}:{slug}:Parameters"));
            }

            // Chat STREAMS (ResponseHeadersRead): a finite HttpClient.Timeout would stay
            // linked to the body stream and kill any generation round longer than it, silently
            // undercutting the turn budget - so it is infinite and the turn budget owns the
            // wall clock (turn-budgets.md section 4a). The Polly pipeline completes at the
            // response headers, so it bounds only the pre-stream phase. The explicit Timeout is
            // set AFTER the resilience handler, which pins it to InfiniteTimeSpan itself.
            var chat = services.AddHttpClient(OpenAISdkClient.HttpClientNameFor(slug));
            chat.AddStandardResilienceHandler().Configure((options, sp) =>
            {
                var parameters = sp.GetRequiredService<IOptionsMonitor<ChatProviderParameters>>().Get(slug);
                ConfigureTransportResilience(options, parameters.RequestTimeoutSeconds, parameters.RetryCount);
            });
            chat.AddHttpMessageHandler<OpenAIWireLogger>();
            chat.ConfigureHttpClient((_, client) => client.Timeout = Timeout.InfiniteTimeSpan);
        }

        // The chat client is built per-provider by IChatClientFactory -> the keyed builder.
    }

    /// <summary>
    /// The provider slugs this plugin owns: every <c>Gert:Chat:Providers</c> entry whose
    /// <c>Type</c> is <c>OpenAI</c> (the default when unset). An empty section means the
    /// synthesized zero-config default, so that one slug is returned - matching the generic
    /// catalog's fallback (both read the same configuration).
    /// </summary>
    private static IReadOnlyList<string> OpenAISlugs(IConfiguration configuration)
    {
        var slugs = configuration.GetSection(ChatProviderOptions.SectionName).GetChildren()
            .Where(c => ChatClientFactory.NormalizeType(c["Type"] ?? "OpenAI")
                == ChatClientFactory.NormalizeType("OpenAI"))
            .Select(c => c.Key)
            .ToList();

        return slugs.Count == 0 ? [ChatProviderInfo.DefaultId] : slugs;
    }

    private static void AddEmbeddings(IServiceCollection services)
    {
        // Embeddings BUFFER: the single Gert:Embeddings connection (base URL + secret bearer),
        // its own options-bound pipeline, but a finite client timeout sits 1 s OUTSIDE the
        // pipeline's total so it covers the buffered body read while the pipeline's timeouts -
        // not this CTS - decide the pre-stream outcomes. After the handler for the same
        // registration-order reason as the chat client (last write wins on Timeout).
        var embeddings = services.AddHttpClient(OpenAIEmbeddingGenerator.HttpClientName);
        embeddings.AddStandardResilienceHandler().Configure((options, sp) =>
        {
            var p = sp.GetRequiredService<IOptions<EmbeddingsOptions>>().Value.Parameters;
            ConfigureTransportResilience(options, p.RequestTimeoutSeconds, p.RetryCount);
        });
        embeddings.ConfigureHttpClient((sp, client) =>
        {
            var p = sp.GetRequiredService<IOptions<EmbeddingsOptions>>().Value.Parameters;
            client.BaseAddress = new Uri(p.BaseUrl, UriKind.Absolute);
            if (!string.IsNullOrEmpty(p.ApiKey))
            {
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", p.ApiKey);
            }

            client.Timeout = TotalTransportTimeout(p.RequestTimeoutSeconds, p.RetryCount) + TimeSpan.FromSeconds(1);
        });

        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp => new OpenAIEmbeddingGenerator(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(OpenAIEmbeddingGenerator.HttpClientName),
            sp.GetRequiredService<IOptions<EmbeddingsOptions>>().Value.Parameters,
            sp.GetRequiredService<ILogger<OpenAIEmbeddingGenerator>>()));
    }

    /// <summary>
    /// Configure the standard resilience pipeline FROM the bound per-item resilience
    /// (dotnet-style-guide.md section 9) - stock defaults would retry on a 10 s time-to-first-byte
    /// (routine GPU queueing) and hard-fail at 30 s, silently undercutting the configured
    /// per-attempt timeout. Both chat and embeddings POSTs complete the pipeline at the response
    /// headers, so every bound here covers only the pre-stream phase.
    /// </summary>
    private static void ConfigureTransportResilience(
        HttpStandardResilienceOptions options, int requestTimeoutSeconds, int retryCount)
    {
        var attempt = TimeSpan.FromSeconds(requestTimeoutSeconds);

        // Per attempt: how long the model may take to ACCEPT the request (time to response
        // headers). Across attempts: the coherent worst case, see TotalTransportTimeout. The
        // stream after the headers is out of scope here.
        options.AttemptTimeout.Timeout = attempt;
        options.TotalRequestTimeout.Timeout = TotalTransportTimeout(requestTimeoutSeconds, retryCount);

        // The pipeline's own validator requires SamplingDuration >= 2 x AttemptTimeout;
        // scale it with the attempt instead of inheriting the stock 30 s.
        options.CircuitBreaker.SamplingDuration = attempt + attempt;

        if (retryCount > 0)
        {
            // Retrying the chat POST here is safe despite non-idempotence: the pipeline
            // completes at the response headers, so a retried attempt means the model never
            // accepted the request and no tokens were streamed. Embedding POSTs are idempotent.
            options.Retry.MaxRetryAttempts = retryCount;
        }
        else
        {
            // RetryCount <= 0 means "no retries"; the pipeline validator forbids
            // MaxRetryAttempts = 0, so disable via the predicate instead.
            options.Retry.ShouldHandle = _ => PredicateResult.False();
        }
    }

    /// <summary>
    /// Worst-case wall clock for the whole pre-stream pipeline: (retryCount + 1) attempts each
    /// running their full <paramref name="requestTimeoutSeconds"/>, plus slack for the
    /// exponential backoff delays between attempts (2 s base => 2-(2^retryCount - 1) s before
    /// jitter; x1.5 headroom). A pathological jitter draw spilling over merely ends an
    /// already-failing call a moment early.
    /// </summary>
    private static TimeSpan TotalTransportTimeout(int requestTimeoutSeconds, int retryCount)
    {
        var attempts = retryCount > 0 ? retryCount + 1 : 1;
        var backoffSlackSeconds = retryCount > 0 ? 3.0 * (Math.Pow(2, retryCount) - 1) : 0.0;
        return TimeSpan.FromSeconds(((double)requestTimeoutSeconds * attempts) + backoffSlackSeconds);
    }

    /// <summary>
    /// Fail-closed <see cref="EmbeddingsOptions.Type"/> discriminator (configuration.md
    /// section 3): only <c>OpenAI</c> ships today, so an unknown Type fails fast at startup
    /// rather than silently using defaults.
    /// </summary>
    private sealed class EmbeddingsTypeValidator : IValidateOptions<EmbeddingsOptions>
    {
        public ValidateOptionsResult Validate(string? name, EmbeddingsOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            if (!string.Equals(options.Type, "OpenAI", StringComparison.OrdinalIgnoreCase))
            {
                return ValidateOptionsResult.Fail(
                    $"{EmbeddingsOptions.SectionName}:Type '{options.Type}' is not supported. Use 'OpenAI'.");
            }

            return ValidateOptionsResult.Success;
        }
    }

    /// <summary>
    /// Fail-closed <see cref="EmbeddingsParameters"/> check, run at startup over the registered
    /// <see cref="EmbeddingsOptions"/> (configuration.md section 3): the connection must be an
    /// absolute http(s) URL, the embedding dimension positive, and the resilience knobs
    /// non-negative - a typo surfaces at boot, named, not on the first embedding call.
    /// </summary>
    private sealed class EmbeddingsParametersValidator : IValidateOptions<EmbeddingsOptions>
    {
        public ValidateOptionsResult Validate(string? name, EmbeddingsOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            var p = options.Parameters;
            var label = $"{EmbeddingsOptions.SectionName}:Parameters";
            var failures = ValidateConnection(label, p.BaseUrl, p.RequestTimeoutSeconds, p.RetryCount).ToList();
            if (p.Dimensions <= 0)
            {
                failures.Add($"{label}:Dimensions must be a positive integer (got {p.Dimensions}).");
            }

            return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
        }
    }

    /// <summary>
    /// Fail-closed per-provider <see cref="ChatProviderParameters"/> check, run at startup over each
    /// configured slug (<paramref name="name"/> is the slug): an absolute http(s) <c>BaseUrl</c> and
    /// non-negative resilience knobs, so a misconfigured provider fails at boot with the slug + knob
    /// named instead of erroring on its first turn.
    /// </summary>
    private sealed class ChatProviderParametersValidator : IValidateOptions<ChatProviderParameters>
    {
        public ValidateOptionsResult Validate(string? name, ChatProviderParameters options)
        {
            ArgumentNullException.ThrowIfNull(options);

            var label = $"{ChatProviderOptions.SectionName}:{name ?? "?"}:Parameters";
            var failures = ValidateConnection(
                label, options.BaseUrl, options.RequestTimeoutSeconds, options.RetryCount).ToList();

            return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
        }
    }

    /// <summary>
    /// The connection-knob checks shared by the embeddings and chat parameter validators: a
    /// parseable absolute http(s) <c>BaseUrl</c> and non-negative timeout/retry. Each failure names
    /// its full config key under <paramref name="label"/>.
    /// </summary>
    private static IEnumerable<string> ValidateConnection(
        string label, string baseUrl, int requestTimeoutSeconds, int retryCount)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)
            || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            yield return $"{label}:BaseUrl must be an absolute http(s) URL (got '{baseUrl}').";
        }

        if (requestTimeoutSeconds < 0)
        {
            yield return $"{label}:RequestTimeoutSeconds must be non-negative (got {requestTimeoutSeconds}).";
        }

        if (retryCount < 0)
        {
            yield return $"{label}:RetryCount must be non-negative (got {retryCount}).";
        }
    }
}
