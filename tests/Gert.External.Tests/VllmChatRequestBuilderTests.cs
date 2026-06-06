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

    [Fact]
    public void Build_SerializesAssistantToolCallsPerOpenAiWireFormat()
    {
        var request = BaseRequest() with
        {
            Messages =
            [
                new ChatModelMessage
                {
                    Role = "assistant",
                    Content = null,
                    ToolCalls =
                    [
                        new ChatModelToolCall { Id = "call_1", Name = "web_search", ArgumentsJson = """{"query":"x"}""" },
                        new ChatModelToolCall { Id = "call_2", Name = "get_datetime", ArgumentsJson = "{}" },
                    ],
                },
                new ChatModelMessage { Role = "tool", Content = "r1", ToolCallId = "call_1" },
                new ChatModelMessage { Role = "tool", Content = "r2", ToolCallId = "call_2" },
            ],
        };

        var body = VllmChatRequestBuilder.Build(request, "m");
        var assistant = body["messages"]!.AsArray()[0]!.AsObject();

        // Tool-call-only assistant turn: no content key, no tool_call_id, a
        // tool_calls array in call order with `arguments` as the raw JSON string.
        assistant.ContainsKey("content").Should().BeFalse();
        assistant.ContainsKey("tool_call_id").Should().BeFalse();

        var calls = assistant["tool_calls"]!.AsArray();
        calls.Should().HaveCount(2);
        calls[0]!["id"]!.GetValue<string>().Should().Be("call_1");
        calls[0]!["type"]!.GetValue<string>().Should().Be("function");
        calls[0]!["function"]!["name"]!.GetValue<string>().Should().Be("web_search");
        calls[0]!["function"]!["arguments"]!.GetValue<string>().Should().Be("""{"query":"x"}""");
        calls[1]!["function"]!["name"]!.GetValue<string>().Should().Be("get_datetime");
    }

    [Fact]
    public void Build_EmitsChatTemplateKwargsOnlyWhenSet()
    {
        var plain = VllmChatRequestBuilder.Build(BaseRequest(), "m");
        plain.ContainsKey("chat_template_kwargs").Should().BeFalse();

        var thinkingOff = VllmChatRequestBuilder.Build(BaseRequest() with { EnableThinking = false }, "m");
        thinkingOff["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>().Should().BeFalse();
        thinkingOff["chat_template_kwargs"]!.AsObject().ContainsKey("preserve_thinking").Should().BeFalse();

        var both = VllmChatRequestBuilder.Build(
            BaseRequest() with { EnableThinking = true, PreserveThinking = true }, "m");
        both["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>().Should().BeTrue();
        both["chat_template_kwargs"]!["preserve_thinking"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void Build_CarriesReasoningContentOnAssistantHistoryButNeverEmpty()
    {
        var request = BaseRequest() with
        {
            Messages =
            [
                new ChatModelMessage { Role = "assistant", Content = "391", ReasoningContent = "17*23 = 391." },
                new ChatModelMessage { Role = "assistant", Content = "ok", ReasoningContent = "" },
            ],
        };

        var body = VllmChatRequestBuilder.Build(request, "m");
        var messages = body["messages"]!.AsArray();
        messages[0]!["reasoning_content"]!.GetValue<string>().Should().Be("17*23 = 391.");
        // Empty reasoning must omit the key — empty <think> blocks drift the
        // rendered prompt (QwenLM/Qwen3.6#131).
        messages[1]!.AsObject().ContainsKey("reasoning_content").Should().BeFalse();
    }

    [Fact]
    public void Build_KeepsContentOnAssistantToolCallTurnWhenPresent()
    {
        // A turn can carry both text and tool calls (the model "thought out loud").
        var request = BaseRequest() with
        {
            Messages =
            [
                new ChatModelMessage
                {
                    Role = "assistant",
                    Content = "let me check",
                    ToolCalls = [new ChatModelToolCall { Id = "c", Name = "web_search", ArgumentsJson = "{}" }],
                },
            ],
        };

        var body = VllmChatRequestBuilder.Build(request, "m");
        var assistant = body["messages"]!.AsArray()[0]!.AsObject();
        assistant["content"]!.GetValue<string>().Should().Be("let me check");
        assistant["tool_calls"]!.AsArray().Should().HaveCount(1);
    }
}
