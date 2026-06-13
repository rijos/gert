using FluentAssertions;
using Gert.External.OpenAI;
using Gert.External.Sandbox;
using Gert.External.Search;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gert.External.Tests;

/// <summary>
/// The HTTP timeout/resilience layering registered by <c>AddGertExternal</c>
/// (dotnet-style-guide.md section 9: a streaming client's <c>HttpClient.Timeout</c> must not cap
/// the stream, and resilience handlers are configured <b>from</b> the bound options, not
/// stock defaults). Pins: chat = infinite client timeout (the turn budget owns the stream,
/// turn-budgets.md section 4a) with an options-bound pre-stream pipeline; embeddings = its own
/// named client with a finite backstop just outside the pipeline total; SearXNG = pipeline
/// total inside the client timeout; monty = HTTP backstop strictly above the sandbox wall
/// clock, enforced at startup.
/// </summary>
public sealed class HttpClientWiringTests
{
    /// <summary>AddStandardResilienceHandler stores its options as "{clientName}-standard".</summary>
    private static string PipelineName(string clientName) => clientName + "-standard";

    private static ServiceProvider Provider(Dictionary<string, string?>? settings = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGertExternal(configuration);
        return services.BuildServiceProvider();
    }

    private static HttpStandardResilienceOptions Pipeline(ServiceProvider provider, string clientName) =>
        provider.GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>()
            .Get(PipelineName(clientName));

    [Fact]
    public void Chat_client_timeout_is_infinite_so_the_turn_budget_owns_the_stream()
    {
        using var provider = Provider();
        var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(OpenAIChatModelClient.HttpClientName);

        client.Timeout.Should().Be(Timeout.InfiniteTimeSpan);
    }

    [Fact]
    public void Embeddings_client_is_split_from_chat_with_a_finite_timeout_above_the_pipeline_total()
    {
        OpenAIEmbeddingClient.HttpClientName.Should().NotBe(
            OpenAIChatModelClient.HttpClientName,
            "the buffered embeddings path must not inherit the streaming client's infinite timeout");

        using var provider = Provider();
        var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(OpenAIEmbeddingClient.HttpClientName);
        var pipeline = Pipeline(provider, OpenAIEmbeddingClient.HttpClientName);

        client.Timeout.Should().NotBe(Timeout.InfiniteTimeSpan);
        // The client timeout is the outermost layer: 1 s outside the Polly total, so
        // the pipeline's timeouts - not the client CTS - decide outcomes.
        client.Timeout.Should().Be(pipeline.TotalRequestTimeout.Timeout + TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData(OpenAIChatModelClient.HttpClientName)]
    [InlineData(OpenAIEmbeddingClient.HttpClientName)]
    public void OpenAI_resilience_pipeline_is_bound_to_the_options_not_stock_defaults(string clientName)
    {
        using var provider = Provider(new Dictionary<string, string?>
        {
            ["Gert:OpenAI:RequestTimeoutSeconds"] = "42",
            ["Gert:OpenAI:RetryCount"] = "4",
        });

        var pipeline = Pipeline(provider, clientName);

        pipeline.AttemptTimeout.Timeout.Should().Be(TimeSpan.FromSeconds(42));
        pipeline.Retry.MaxRetryAttempts.Should().Be(4);
        // Total = attempt x (retries + 1) + backoff slack 3-(2^retries - 1) s:
        // 42-5 + 3-15 = 255 s - coherent, instead of the stock 30 s that silently
        // undercut the configured per-attempt timeout.
        pipeline.TotalRequestTimeout.Timeout.Should().Be(TimeSpan.FromSeconds(255));
        // The standard validator demands SamplingDuration >= 2 x attempt.
        pipeline.CircuitBreaker.SamplingDuration.Should().Be(TimeSpan.FromSeconds(84));
    }

    [Fact]
    public void OpenAI_retry_count_zero_is_accepted_and_disables_retries()
    {
        using var provider = Provider(new Dictionary<string, string?>
        {
            ["Gert:OpenAI:RetryCount"] = "0",
        });

        // Resolving the named options runs the pipeline validators; MaxRetryAttempts = 0
        // would be rejected, so zero is implemented via a never-handle predicate instead.
        var pipeline = Pipeline(provider, OpenAIChatModelClient.HttpClientName);

        pipeline.TotalRequestTimeout.Timeout.Should().Be(
            pipeline.AttemptTimeout.Timeout, "a single attempt is the whole budget");
    }

    [Fact]
    public void Searxng_pipeline_total_sits_inside_the_client_timeout()
    {
        using var provider = Provider(new Dictionary<string, string?>
        {
            ["Gert:Search:SearchTimeoutSeconds"] = "5",
        });

        var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(SearXngWebSearch.HttpClientName);
        var pipeline = Pipeline(provider, SearXngWebSearch.HttpClientName);

        // SearchTimeoutSeconds is the whole-call budget (Polly total); the client
        // timeout is a backstop 1 s above it - previously the 15 s client timeout sat
        // INSIDE Polly's stock 30 s total.
        pipeline.TotalRequestTimeout.Timeout.Should().Be(TimeSpan.FromSeconds(5));
        client.Timeout.Should().Be(TimeSpan.FromSeconds(6));
        pipeline.AttemptTimeout.Timeout.Should().BeLessThanOrEqualTo(pipeline.TotalRequestTimeout.Timeout);
    }

    [Fact]
    public void Monty_defaults_satisfy_the_wall_clock_relation()
    {
        using var provider = Provider();

        var monty = provider.GetRequiredService<IOptions<MontyOptions>>().Value;

        monty.RequestTimeoutSeconds.Should().BeGreaterThan(
            provider.GetRequiredService<IOptions<PythonSandboxOptions>>().Value.WallClockSeconds,
            "the HTTP backstop must trip only after monty's own wall clock");
    }

    [Fact]
    public void Monty_request_timeout_at_or_below_the_sandbox_wall_clock_fails_startup_validation()
    {
        using var provider = Provider(new Dictionary<string, string?>
        {
            // Wall clock 60 s vs the default 30 s HTTP backstop: the transport would
            // kill runs the interpreter was about to return cleanly.
            ["Gert:Sandbox:WallClockSeconds"] = "60",
        });

        var act = () => provider.GetRequiredService<IOptions<MontyOptions>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*RequestTimeoutSeconds*")
            .WithMessage("*WallClockSeconds*");
    }

    [Fact]
    public void Wall_clock_relation_is_not_enforced_for_the_gvisor_backend()
    {
        using var provider = Provider(new Dictionary<string, string?>
        {
            ["Gert:Sandbox:Backend"] = "gvisor",
            ["Gert:Sandbox:WallClockSeconds"] = "60",
        });

        // gVisor does not use the monty sidecar, so the relation validator is only
        // registered on the monty path - a long gVisor wall clock must not demand a
        // pointless monty knob change.
        var act = () => provider.GetRequiredService<IOptions<MontyOptions>>().Value;

        act.Should().NotThrow();
    }
}
