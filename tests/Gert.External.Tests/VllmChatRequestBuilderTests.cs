using System.Text.Json.Nodes;
using FluentAssertions;
using Gert.External.Vllm;
using Gert.Service.External;
using Xunit;

namespace Gert.External.Tests;

/// <summary>
/// Unit tests for the pure chat request-body builder: model, messages, advertised tools,
/// <c>stream:true</c>, and sampling params land in the right OpenAI-compatible shape.
/// </summary>
public sealed class VllmChatRequestBuilderTests
{
    private static ChatCompletionRequest BaseRequest() => new()
    {
        ModelId = "ignored-port-id",
        Messages =
        [
            new ChatModelMessage { Role = "system", Content = "be terse" },
            new ChatModelMessage { Role = "user", Content = "hi" },
        ],
    };

    [Fact]
    public void Build_SetsModelMessagesAndStream()
    {
        var body = VllmChatRequestBuilder.Build(BaseRequest(), "qwen-2.5");

        body["model"]!.GetValue<string>().Should().Be("qwen-2.5");
        body["stream"]!.GetValue<bool>().Should().BeTrue();

        var messages = body["messages"]!.AsArray();
        messages.Should().HaveCount(2);
        messages[0]!["role"]!.GetValue<string>().Should().Be("system");
        messages[1]!["content"]!.GetValue<string>().Should().Be("hi");
    }

    [Fact]
    public void Build_RequestsUsageInStream()
    {
        var body = VllmChatRequestBuilder.Build(BaseRequest(), "m");
        body["stream_options"]!["include_usage"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void Build_AdvertisesToolsAsFunctions()
    {
        var request = BaseRequest() with
        {
            Tools =
            [
                new ChatToolSpec
                {
                    Name = "web_search",
                    Description = "search the web",
                    ParametersSchema = """{"type":"object","properties":{"q":{"type":"string"}}}""",
                },
            ],
        };

        var body = VllmChatRequestBuilder.Build(request, "m");
        var tools = body["tools"]!.AsArray();
        tools.Should().HaveCount(1);

        var fn = tools[0]!["function"]!;
        tools[0]!["type"]!.GetValue<string>().Should().Be("function");
        fn["name"]!.GetValue<string>().Should().Be("web_search");
        fn["parameters"]!["properties"]!["q"]!["type"]!.GetValue<string>().Should().Be("string");
    }

    [Fact]
    public void Build_MalformedToolSchema_DegradesToEmptyObject()
    {
        var request = BaseRequest() with
        {
            Tools = [new ChatToolSpec { Name = "t", Description = "d", ParametersSchema = "{not json" }],
        };

        var body = VllmChatRequestBuilder.Build(request, "m");
        var parameters = body["tools"]!.AsArray()[0]!["function"]!["parameters"]!;
        parameters["type"]!.GetValue<string>().Should().Be("object");
    }

    [Fact]
    public void Build_IncludesSamplingParamsWhenSet()
    {
        var request = BaseRequest() with
        {
            Temperature = 0.2,
            TopP = 0.9,
            MaxTokens = 128,
            Seed = 42,
            Stop = ["END"],
        };

        var body = VllmChatRequestBuilder.Build(request, "m");
        body["temperature"]!.GetValue<double>().Should().Be(0.2);
        body["top_p"]!.GetValue<double>().Should().Be(0.9);
        body["max_tokens"]!.GetValue<int>().Should().Be(128);
        body["seed"]!.GetValue<int>().Should().Be(42);
        body["stop"]!.AsArray()[0]!.GetValue<string>().Should().Be("END");
    }

    [Fact]
    public void Build_OmitsToolsWhenNone()
    {
        var body = VllmChatRequestBuilder.Build(BaseRequest(), "m");
        (body["tools"] as JsonArray).Should().BeNull();
    }

    [Fact]
    public void Build_CarriesToolCallIdOnToolMessages()
    {
        var request = BaseRequest() with
        {
            Messages =
            [
                new ChatModelMessage { Role = "tool", Content = "result", ToolCallId = "call_9" },
            ],
        };

        var body = VllmChatRequestBuilder.Build(request, "m");
        body["messages"]!.AsArray()[0]!["tool_call_id"]!.GetValue<string>().Should().Be("call_9");
    }
}
