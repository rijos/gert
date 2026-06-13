using FluentAssertions;
using Gert.External.OpenAI;
using Gert.External.Providers;
using Gert.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gert.External.Tests;

/// <summary>
/// <see cref="ConfigChatProviderCatalog"/> - the <c>Gert:Providers</c> map binding, the
/// single-vLLM fallback, the PERMISSIVE <c>SupportsTools</c> semantics (only declared
/// capabilities without <c>tools</c> gate), and <see cref="ConfigChatProviderCatalog.Resolve"/>
/// handing the chat-client factory each provider's connection + sampling.
/// </summary>
public sealed class ConfigChatProviderCatalogTests
{
    private static ConfigChatProviderCatalog Catalog(
        Dictionary<string, string?>? settings = null,
        string baseUrl = "http://localhost:8000") =>
        new(
            new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build(),
            Options.Create(new OpenAIOptions { BaseUrl = baseUrl }));

    [Fact]
    public void Empty_config_falls_back_to_a_default_openai_provider_with_tools()
    {
        var catalog = Catalog(baseUrl: "http://vllm:9000");

        catalog.List().Should().ContainSingle(p => p.Id == ChatProviderInfo.DefaultId && p.Default);
        catalog.SupportsTools(ChatProviderInfo.DefaultId).Should().BeTrue();

        // The fallback resolves to the bound base URL + the "default" upstream model.
        var (id, options) = catalog.Resolve(null);
        id.Should().Be(ChatProviderInfo.DefaultId);
        options.Type.Should().Be("openai");
        options.Parameters.BaseUrl.Should().Be("http://vllm:9000");
        options.Parameters.Model.Should().Be("default");
    }

    [Fact]
    public void Configured_providers_bind_and_resolve_their_connection_and_sampling()
    {
        var catalog = Catalog(new Dictionary<string, string?>
        {
            ["Gert:Providers:qwen36-thinking:Name"] = "Qwen36 - thinking",
            ["Gert:Providers:qwen36-thinking:Type"] = "openai",
            ["Gert:Providers:qwen36-thinking:Default"] = "true",
            ["Gert:Providers:qwen36-thinking:Capabilities:0"] = "tools",
            ["Gert:Providers:qwen36-thinking:Parameters:BaseUrl"] = "http://vllm:8000",
            ["Gert:Providers:qwen36-thinking:Parameters:Model"] = "qwen36",
            ["Gert:Providers:qwen36-thinking:Parameters:Temperature"] = "0.6",
            ["Gert:Providers:qwen36-thinking:Parameters:TopP"] = "0.95",
            ["Gert:Providers:qwen36-thinking:Parameters:Extra:top_k"] = "20",
            ["Gert:Providers:qwen36-thinking:Parameters:Extra:chat_template_kwargs.enable_thinking"] = "true",
        });

        catalog.List().Should().ContainSingle(p => p.Id == "qwen36-thinking" && p.Name == "Qwen36 - thinking");

        var (id, options) = catalog.Resolve("qwen36-thinking");
        id.Should().Be("qwen36-thinking");
        options.Parameters.Model.Should().Be("qwen36");
        options.Parameters.Temperature.Should().Be(0.6);
        options.Parameters.TopP.Should().Be(0.95);
        options.Parameters.Extra["top_k"].Should().Be("20");
        options.Parameters.Extra["chat_template_kwargs.enable_thinking"].Should().Be("true");
        options.Parameters.PreserveThinking.Should().BeFalse();
    }

    [Fact]
    public void Default_resolves_the_flagged_entry_and_the_sentinel_and_unknown_ids_fall_back_to_it()
    {
        var catalog = Catalog(new Dictionary<string, string?>
        {
            ["Gert:Providers:a:Name"] = "A",
            ["Gert:Providers:a:Parameters:Model"] = "model-a",
            ["Gert:Providers:b:Name"] = "B",
            ["Gert:Providers:b:Default"] = "true",
            ["Gert:Providers:b:Parameters:Model"] = "model-b",
        });

        catalog.Default()!.Id.Should().Be("b");
        catalog.Resolve(ChatProviderInfo.DefaultId).Id.Should().Be("b");
        catalog.Resolve("not-a-provider").Id.Should().Be("b");
        catalog.Resolve("a").Options.Parameters.Model.Should().Be("model-a");
    }

    [Fact]
    public void Declared_capabilities_without_tools_gate_tool_calling()
    {
        var catalog = Catalog(new Dictionary<string, string?>
        {
            ["Gert:Providers:echo:Name"] = "Echo Server",
            ["Gert:Providers:echo:Capabilities:0"] = "text only",
            ["Gert:Providers:qwen:Name"] = "Qwen",
            ["Gert:Providers:qwen:Capabilities:0"] = "tools",
            ["Gert:Providers:mystery:Name"] = "No caps declared",
        });

        catalog.SupportsTools("echo").Should().BeFalse();   // declared, no "tools"
        catalog.SupportsTools("qwen").Should().BeTrue();    // declared with "tools"
        catalog.SupportsTools("mystery").Should().BeTrue(); // undeclared -> permissive
    }
}
