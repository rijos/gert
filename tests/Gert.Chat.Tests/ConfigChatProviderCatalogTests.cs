using FluentAssertions;
using Gert.Model.Chat;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Gert.Chat.Tests;

/// <summary>
/// <see cref="ConfigChatProviderCatalog"/> - the implementation-agnostic <c>Gert:Chat:Providers</c>
/// map binding, the synthesized-default fallback (contributed by an <see cref="IDefaultChatProvider"/>
/// plugin), the PERMISSIVE <c>SupportsTools</c> semantics (only declared capabilities without
/// <c>tools</c> gate), and <see cref="ConfigChatProviderCatalog.Resolve"/> handing the chat-client
/// factory each provider's id + Type. The type-specific connection + sampling are bound by the
/// chosen plugin (see <c>OpenAIPluginTests</c>), not by the catalog.
/// </summary>
public sealed class ConfigChatProviderCatalogTests
{
    /// <summary>
    /// Stands in for the OpenAI plugin's zero-config contribution: a single permissive default
    /// provider pointed at the given base URL. The catalog only consults this when the map is empty.
    /// </summary>
    private sealed class StubDefault(string baseUrl) : IDefaultChatProvider
    {
        public ChatProviderInfo? Synthesize() => new()
        {
            Id = ChatProviderInfo.DefaultId,
            Name = "Default",
            Type = "OpenAI",
            Default = true,
            Capabilities = [ChatProviderInfo.ToolsCapability, ChatProviderInfo.VisionCapability],
            Endpoint = baseUrl,
        };
    }

    private static ConfigChatProviderCatalog Catalog(
        Dictionary<string, string?>? settings = null,
        string baseUrl = "http://localhost:8000") =>
        new(
            new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build(),
            new StubDefault(baseUrl));

    [Fact]
    public void Empty_config_falls_back_to_the_synthesized_default_provider_with_tools()
    {
        var catalog = Catalog(baseUrl: "http://vllm:9000");

        catalog.List().Should().ContainSingle(p => p.Id == ChatProviderInfo.DefaultId && p.Default);
        catalog.SupportsTools(ChatProviderInfo.DefaultId).Should().BeTrue();

        // The fallback resolves to the synthesized default: id + Type for the factory to dispatch,
        // and the display endpoint carried through from the default provider plugin.
        var info = catalog.Resolve(null);
        info.Id.Should().Be(ChatProviderInfo.DefaultId);
        info.Type.Should().Be("OpenAI");
        info.Endpoint.Should().Be("http://vllm:9000");
    }

    [Fact]
    public void Without_a_default_provider_plugin_an_empty_map_yields_an_empty_catalog()
    {
        var catalog = new ConfigChatProviderCatalog(new ConfigurationBuilder().Build());

        catalog.List().Should().BeEmpty();
        var resolve = () => catalog.Resolve(null);
        resolve.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Configured_providers_bind_their_metadata_and_endpoint()
    {
        var catalog = Catalog(new Dictionary<string, string?>
        {
            ["Gert:Chat:Providers:qwen36-thinking:Name"] = "Qwen36 - thinking",
            ["Gert:Chat:Providers:qwen36-thinking:Type"] = "openai",
            ["Gert:Chat:Providers:qwen36-thinking:Capabilities:0"] = "tools",
            // The catalog reads only the endpoint generically from Parameters:BaseUrl; the rest
            // of Parameters (Model/Temperature/Extra) is the OpenAI plugin's, not the catalog's.
            ["Gert:Chat:Providers:qwen36-thinking:Parameters:BaseUrl"] = "http://vllm:8000",
            ["Gert:Chat:Providers:qwen36-thinking:Parameters:Model"] = "qwen36",
        });

        catalog.List().Should().ContainSingle(p => p.Id == "qwen36-thinking" && p.Name == "Qwen36 - thinking");

        var info = catalog.Resolve("qwen36-thinking");
        info.Id.Should().Be("qwen36-thinking");
        info.Type.Should().Be("openai");
        info.Endpoint.Should().Be("http://vllm:8000");
    }

    [Fact]
    public void DefaultProvider_names_the_default_and_the_sentinel_and_unknown_ids_fall_back_to_it()
    {
        var catalog = Catalog(new Dictionary<string, string?>
        {
            ["Gert:Chat:Providers:a:Name"] = "A",
            ["Gert:Chat:Providers:b:Name"] = "B",
            ["Gert:Chat:DefaultProvider"] = "b",
        });

        catalog.Default()!.Id.Should().Be("b");
        catalog.List().Should().ContainSingle(p => p.Id == "b" && p.Default);
        catalog.List().Should().Contain(p => p.Id == "a" && !p.Default);
        catalog.Resolve(ChatProviderInfo.DefaultId).Id.Should().Be("b");
        catalog.Resolve("not-a-provider").Id.Should().Be("b");
        catalog.Resolve("a").Id.Should().Be("a");
    }

    [Fact]
    public void DefaultProvider_match_is_case_insensitive()
    {
        var catalog = Catalog(new Dictionary<string, string?>
        {
            ["Gert:Chat:Providers:a:Name"] = "A",
            ["Gert:Chat:Providers:b:Name"] = "B",
            ["Gert:Chat:DefaultProvider"] = "B",
        });

        catalog.Default()!.Id.Should().Be("b");
    }

    [Fact]
    public void Without_DefaultProvider_the_first_configured_entry_is_the_default()
    {
        var catalog = Catalog(new Dictionary<string, string?>
        {
            ["Gert:Chat:Providers:a:Name"] = "A",
            ["Gert:Chat:Providers:b:Name"] = "B",
        });

        catalog.Default()!.Id.Should().Be("a");
        catalog.List().Should().OnlyContain(p => !p.Default);
    }

    [Fact]
    public void An_unknown_DefaultProvider_name_fails_closed_naming_the_valid_slugs()
    {
        var build = () => Catalog(new Dictionary<string, string?>
        {
            ["Gert:Chat:Providers:a:Name"] = "A",
            ["Gert:Chat:Providers:b:Name"] = "B",
            ["Gert:Chat:DefaultProvider"] = "typo",
        });

        build.Should().Throw<InvalidOperationException>()
            .WithMessage("*DefaultProvider*typo*")
            .Which.Message.Should().Contain("a, b");
    }

    [Fact]
    public void Declared_capabilities_without_tools_gate_tool_calling()
    {
        var catalog = Catalog(new Dictionary<string, string?>
        {
            ["Gert:Chat:Providers:echo:Name"] = "Echo Server",
            ["Gert:Chat:Providers:echo:Capabilities:0"] = "text only",
            ["Gert:Chat:Providers:qwen:Name"] = "Qwen",
            ["Gert:Chat:Providers:qwen:Capabilities:0"] = "tools",
            ["Gert:Chat:Providers:mystery:Name"] = "No caps declared",
        });

        catalog.SupportsTools("echo").Should().BeFalse();   // declared, no "tools"
        catalog.SupportsTools("qwen").Should().BeTrue();    // declared with "tools"
        catalog.SupportsTools("mystery").Should().BeTrue(); // undeclared -> permissive
    }
}
