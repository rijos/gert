using FluentAssertions;
using Gert.External;
using Gert.External.Vllm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gert.External.Tests;

/// <summary>
/// <see cref="ConfigModelCatalog"/> — the <c>Gert:Models</c> binding, the
/// single-vLLM fallback, and the PERMISSIVE <c>SupportsTools</c> semantics
/// (only declared capabilities without <c>tools</c> gate).
/// </summary>
public sealed class ConfigModelCatalogTests
{
    private static ConfigModelCatalog Catalog(
        Dictionary<string, string?>? settings = null,
        string chatModelId = "default") =>
        new(
            new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build(),
            Options.Create(new VllmOptions { ChatModelId = chatModelId }));

    [Fact]
    public void Empty_config_falls_back_to_the_vllm_chat_model_with_tools()
    {
        var catalog = Catalog(chatModelId: "qwen-test");

        catalog.List().Should().ContainSingle(m => m.Id == "qwen-test" && m.Default);
        catalog.SupportsTools("qwen-test").Should().BeTrue();
    }

    [Fact]
    public void Fallback_entry_declares_the_qwen_instruct_sampling()
    {
        // The single-vLLM fallback serves Qwen3.6, whose generation_config
        // only carries thinking-mode sampling — the catalog must declare the
        // card's instruct set for thinking-off turns.
        var instruct = Catalog().InstructParams("default");

        instruct.Should().NotBeNull();
        instruct!.Temperature.Should().Be(0.7);
        instruct.TopP.Should().Be(0.8);
        instruct.PresencePenalty.Should().Be(1.5);
    }

    [Fact]
    public void Configured_models_bind_and_scope_their_instruct_sampling()
    {
        var catalog = Catalog(new Dictionary<string, string?>
        {
            ["Gert:Models:0:Id"] = "qwen",
            ["Gert:Models:0:Name"] = "Qwen",
            ["Gert:Models:0:InstructParams:Temperature"] = "0.7",
            ["Gert:Models:0:InstructParams:TopP"] = "0.8",
            ["Gert:Models:0:InstructParams:PresencePenalty"] = "1.5",
            ["Gert:Models:1:Id"] = "other",
            ["Gert:Models:1:Name"] = "No instruct set",
        });

        var instruct = catalog.InstructParams("qwen");
        instruct.Should().NotBeNull();
        instruct!.Temperature.Should().Be(0.7);
        instruct.TopP.Should().Be(0.8);
        instruct.PresencePenalty.Should().Be(1.5);

        catalog.InstructParams("other").Should().BeNull("no declared instruct set");
        catalog.InstructParams("unknown-id").Should().BeNull();
    }

    [Fact]
    public void Declared_capabilities_without_tools_gate_tool_calling()
    {
        var catalog = Catalog(new Dictionary<string, string?>
        {
            ["Gert:Models:0:Id"] = "echo",
            ["Gert:Models:0:Name"] = "Echo Server",
            ["Gert:Models:0:Capabilities:0"] = "text only",
            ["Gert:Models:1:Id"] = "qwen",
            ["Gert:Models:1:Name"] = "Qwen",
            ["Gert:Models:1:Capabilities:0"] = "tools",
            ["Gert:Models:2:Id"] = "mystery",
            ["Gert:Models:2:Name"] = "No caps declared",
        });

        catalog.SupportsTools("echo").Should().BeFalse();   // declared, no "tools"
        catalog.SupportsTools("qwen").Should().BeTrue();    // declared with "tools"
        catalog.SupportsTools("mystery").Should().BeTrue(); // undeclared → permissive
        catalog.SupportsTools("unknown-id").Should().BeTrue(); // not in catalog → permissive
    }
}
