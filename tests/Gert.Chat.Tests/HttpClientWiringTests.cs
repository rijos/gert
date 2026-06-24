using FluentAssertions;
using Gert.Chat.OpenAI;
using Gert.Model;
using Gert.Model.Chat;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gert.Chat.Tests;

/// <summary>
/// The HTTP timeout/resilience layering registered by <c>AddGertChat</c>. Pins:
/// chat = infinite client timeout (the turn budget owns the stream, turn-budgets.md section
/// 4a; dotnet-style-guide.md section 9: a streaming client's <c>HttpClient.Timeout</c> must
/// not cap the stream) with a per-provider pre-stream pipeline - <b>one named client per
/// provider slug</b>, not a single shared "openai" client; embeddings = its own named client
/// with a finite backstop just outside the pipeline total. Resilience is configured <b>from</b>
/// the bound options per item: chat reads each provider's <c>Parameters</c>, embeddings reads
/// <c>Gert:Embeddings:Parameters</c>.
/// </summary>
public sealed class HttpClientWiringTests
{
    /// <summary>AddStandardResilienceHandler stores its options as "{clientName}-standard".</summary>
    private static string PipelineName(string clientName) => clientName + "-standard";

    private static string DefaultChatClient => OpenAISdkClient.HttpClientNameFor(ChatProviderInfo.DefaultId);

    private static ServiceProvider Provider(Dictionary<string, string?>? settings = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        // The generic chat layer + the OpenAI implementation plugin (the composition root wires
        // both): the per-provider transports under test are registered by AddGertChatOpenAI.
        services.AddGertChat(configuration);
        services.AddGertChatOpenAI(configuration);
        return services.BuildServiceProvider();
    }

    private static HttpStandardResilienceOptions Pipeline(ServiceProvider provider, string clientName) =>
        provider.GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>()
            .Get(PipelineName(clientName));

    [Fact]
    public void Chat_client_timeout_is_infinite_so_the_turn_budget_owns_the_stream()
    {
        using var provider = Provider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(DefaultChatClient);

        client.Timeout.Should().Be(Timeout.InfiniteTimeSpan);
    }

    [Fact]
    public void Chat_transport_is_one_named_client_per_provider_slug()
    {
        using var provider = Provider(new Dictionary<string, string?>
        {
            ["Gert:Chat:Providers:fast:Parameters:RequestTimeoutSeconds"] = "10",
            ["Gert:Chat:Providers:fast:Parameters:RetryCount"] = "1",
            ["Gert:Chat:Providers:slow:Parameters:RequestTimeoutSeconds"] = "42",
            ["Gert:Chat:Providers:slow:Parameters:RetryCount"] = "4",
        });

        // Each slug gets its own pipeline bound to its Parameters - never a shared "openai" client.
        Pipeline(provider, OpenAISdkClient.HttpClientNameFor("fast")).AttemptTimeout.Timeout
            .Should().Be(TimeSpan.FromSeconds(10));
        Pipeline(provider, OpenAISdkClient.HttpClientNameFor("slow")).AttemptTimeout.Timeout
            .Should().Be(TimeSpan.FromSeconds(42));
        Pipeline(provider, OpenAISdkClient.HttpClientNameFor("slow")).Retry.MaxRetryAttempts
            .Should().Be(4);
    }

    [Fact]
    public void Chat_resilience_pipeline_is_bound_to_the_provider_parameters_not_stock_defaults()
    {
        using var provider = Provider(new Dictionary<string, string?>
        {
            ["Gert:Chat:Providers:p:Parameters:RequestTimeoutSeconds"] = "42",
            ["Gert:Chat:Providers:p:Parameters:RetryCount"] = "4",
        });

        var pipeline = Pipeline(provider, OpenAISdkClient.HttpClientNameFor("p"));

        pipeline.AttemptTimeout.Timeout.Should().Be(TimeSpan.FromSeconds(42));
        pipeline.Retry.MaxRetryAttempts.Should().Be(4);
        // Total = attempt x (retries + 1) + backoff slack 3-(2^retries - 1) s:
        // 42-5 + 3-15 = 255 s - coherent, instead of the stock 30 s.
        pipeline.TotalRequestTimeout.Timeout.Should().Be(TimeSpan.FromSeconds(255));
        pipeline.CircuitBreaker.SamplingDuration.Should().Be(TimeSpan.FromSeconds(84));
    }

    [Fact]
    public void Embeddings_client_is_split_from_chat_with_a_finite_timeout_above_the_pipeline_total()
    {
        OpenAIEmbeddingGenerator.HttpClientName.Should().NotBe(
            DefaultChatClient,
            "the buffered embeddings path must not inherit a streaming chat client's infinite timeout");

        using var provider = Provider();
        var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(OpenAIEmbeddingGenerator.HttpClientName);
        var pipeline = Pipeline(provider, OpenAIEmbeddingGenerator.HttpClientName);

        client.Timeout.Should().NotBe(Timeout.InfiniteTimeSpan);
        // The client timeout is the outermost layer: 1 s outside the Polly total, so
        // the pipeline's timeouts - not the client CTS - decide outcomes.
        client.Timeout.Should().Be(pipeline.TotalRequestTimeout.Timeout + TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Embeddings_resilience_pipeline_is_bound_to_the_options_not_stock_defaults()
    {
        using var provider = Provider(new Dictionary<string, string?>
        {
            ["Gert:Embeddings:Parameters:RequestTimeoutSeconds"] = "42",
            ["Gert:Embeddings:Parameters:RetryCount"] = "4",
        });

        var pipeline = Pipeline(provider, OpenAIEmbeddingGenerator.HttpClientName);

        pipeline.AttemptTimeout.Timeout.Should().Be(TimeSpan.FromSeconds(42));
        pipeline.Retry.MaxRetryAttempts.Should().Be(4);
        pipeline.TotalRequestTimeout.Timeout.Should().Be(TimeSpan.FromSeconds(255));
        // The standard validator demands SamplingDuration >= 2 x attempt.
        pipeline.CircuitBreaker.SamplingDuration.Should().Be(TimeSpan.FromSeconds(84));
    }

    [Fact]
    public void Embeddings_retry_count_zero_is_accepted_and_disables_retries()
    {
        using var provider = Provider(new Dictionary<string, string?>
        {
            ["Gert:Embeddings:Parameters:RetryCount"] = "0",
        });

        // Resolving the named options runs the pipeline validators; MaxRetryAttempts = 0
        // would be rejected, so zero is implemented via a never-handle predicate instead.
        var pipeline = Pipeline(provider, OpenAIEmbeddingGenerator.HttpClientName);

        pipeline.TotalRequestTimeout.Timeout.Should().Be(
            pipeline.AttemptTimeout.Timeout, "a single attempt is the whole budget");
    }
}
