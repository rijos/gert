using FluentAssertions;
using Gert.Chat.OpenAI;
using Gert.Model.Chat;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gert.Chat.Tests;

/// <summary>
/// The OpenAI chat IMPLEMENTATION plugin (<c>AddGertChatOpenAI</c>): each provider slug's
/// connection + sampling bind as NAMED <see cref="ChatProviderParameters"/> options (keyed by
/// the slug); the zero-config default slug takes its base URL from <c>Gert:Embeddings</c>; and
/// the generic keyed <see cref="IChatClientFactory"/> dispatches to this plugin by the provider's
/// <c>Type</c> - building an <see cref="OpenAIChatModelClient"/> with no central switch over Type.
/// </summary>
public sealed class OpenAIPluginTests
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

    private static ChatProviderParameters ParametersFor(ServiceProvider provider, string slug) =>
        provider.GetRequiredService<IOptionsMonitor<ChatProviderParameters>>().Get(slug);

    [Fact]
    public void Each_provider_slug_binds_its_own_named_connection_and_sampling()
    {
        using var provider = Provider(new Dictionary<string, string?>
        {
            ["Gert:Chat:Providers:qwen36-thinking:Parameters:BaseUrl"] = "http://vllm:8000",
            ["Gert:Chat:Providers:qwen36-thinking:Parameters:Model"] = "qwen36",
            ["Gert:Chat:Providers:qwen36-thinking:Parameters:Temperature"] = "0.6",
            ["Gert:Chat:Providers:qwen36-thinking:Parameters:TopP"] = "0.95",
            ["Gert:Chat:Providers:qwen36-thinking:Parameters:Extra:top_k"] = "20",
            ["Gert:Chat:Providers:qwen36-thinking:Parameters:Extra:chat_template_kwargs.enable_thinking"] = "true",
        });

        var parameters = ParametersFor(provider, "qwen36-thinking");

        parameters.BaseUrl.Should().Be("http://vllm:8000");
        parameters.Model.Should().Be("qwen36");
        parameters.Temperature.Should().Be(0.6);
        parameters.TopP.Should().Be(0.95);
        parameters.Extra["top_k"].Should().Be("20");
        parameters.Extra["chat_template_kwargs.enable_thinking"].Should().Be("true");
        parameters.PreserveThinking.Should().BeFalse();
    }

    [Fact]
    public void Zero_config_default_slug_takes_its_base_url_from_embeddings()
    {
        using var provider = Provider(new Dictionary<string, string?>
        {
            ["Gert:Embeddings:Parameters:BaseUrl"] = "http://vllm:9000",
        });

        var parameters = ParametersFor(provider, ChatProviderInfo.DefaultId);

        parameters.BaseUrl.Should().Be("http://vllm:9000");
        parameters.Model.Should().Be("default");
    }

    [Fact]
    public void The_factory_dispatches_to_the_openai_plugin_by_type_with_no_central_switch()
    {
        using var provider = Provider(new Dictionary<string, string?>
        {
            ["Gert:Embeddings:Parameters:BaseUrl"] = "http://vllm:9000",
        });

        var client = provider.GetRequiredService<IChatClientFactory>().ForProvider(null);

        client.Should().BeOfType<OpenAIChatModelClient>();
    }
}
