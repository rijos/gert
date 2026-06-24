using FluentAssertions;
using Gert.Chat.OpenAI;
using Gert.Model.Chat;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gert.Chat.Tests;

/// <summary>
/// Fail-closed options validation for the OpenAI plugin (configuration.md section 3): a misconfigured
/// embeddings or chat-provider connection is rejected when its options are realized (the same check
/// <c>ValidateOnStart</c> runs at boot), naming the offending knob - so a typo'd upstream fails fast
/// instead of on the first turn/upload.
/// </summary>
public sealed class OpenAIOptionsValidationTests
{
    private static ServiceProvider Provider(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGertChat(configuration);
        services.AddGertChatOpenAI(configuration);
        return services.BuildServiceProvider();
    }

    private static EmbeddingsParameters Embeddings(ServiceProvider p) =>
        p.GetRequiredService<IOptions<EmbeddingsOptions>>().Value.Parameters;

    private static ChatProviderParameters Chat(ServiceProvider p, string slug) =>
        p.GetRequiredService<IOptionsMonitor<ChatProviderParameters>>().Get(slug);

    [Fact]
    public void Valid_config_realizes_without_error()
    {
        using var p = Provider(new Dictionary<string, string?>
        {
            ["Gert:Embeddings:Parameters:BaseUrl"] = "http://vllm:8000",
            ["Gert:Chat:Providers:qwen:Parameters:BaseUrl"] = "http://vllm:8000",
            ["Gert:Chat:Providers:qwen:Context"] = "131072",
        });

        Embeddings(p).BaseUrl.Should().Be("http://vllm:8000");
        Chat(p, "qwen").BaseUrl.Should().Be("http://vllm:8000");
    }

    [Theory]
    [InlineData(":8000")] // port-only, not absolute
    [InlineData("vllm:8000")] // host:port, no scheme
    [InlineData("ftp://vllm:8000")] // wrong scheme
    [InlineData("")] // empty
    public void Embeddings_with_a_non_absolute_http_base_url_fails(string baseUrl)
    {
        using var p = Provider(new Dictionary<string, string?>
        {
            ["Gert:Embeddings:Parameters:BaseUrl"] = baseUrl,
        });

        var act = () => Embeddings(p);
        act.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("BaseUrl");
    }

    [Fact]
    public void Embeddings_with_a_non_positive_dimension_fails()
    {
        using var p = Provider(new Dictionary<string, string?>
        {
            ["Gert:Embeddings:Parameters:BaseUrl"] = "http://vllm:8000",
            ["Gert:Embeddings:Parameters:Dimensions"] = "0",
        });

        var act = () => Embeddings(p);
        act.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("Dimensions");
    }

    [Fact]
    public void A_chat_provider_with_a_relative_base_url_fails_naming_the_slug()
    {
        using var p = Provider(new Dictionary<string, string?>
        {
            ["Gert:Chat:Providers:qwen:Parameters:BaseUrl"] = ":8001",
            ["Gert:Chat:Providers:qwen:Context"] = "131072",
        });

        var act = () => Chat(p, "qwen");
        act.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("qwen").And.Contain("BaseUrl");
    }

    [Fact]
    public void A_chat_provider_with_a_negative_retry_count_fails()
    {
        using var p = Provider(new Dictionary<string, string?>
        {
            ["Gert:Chat:Providers:qwen:Parameters:BaseUrl"] = "http://vllm:8000",
            ["Gert:Chat:Providers:qwen:Parameters:RetryCount"] = "-1",
            ["Gert:Chat:Providers:qwen:Context"] = "131072",
        });

        var act = () => Chat(p, "qwen");
        act.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("RetryCount");
    }
}
