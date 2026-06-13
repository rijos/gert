using System.ClientModel.Primitives;
using System.Text.Json.Nodes;
using FluentAssertions;
using Gert.External.OpenAI;
using Gert.External.Providers;
using Gert.Service.External;
using OpenAI.Chat;
using Xunit;

namespace Gert.External.Tests;

/// <summary>
/// Unit tests for the pure port-DTO -> SDK request mapper: messages, advertised tools,
/// <c>tool_choice:"auto"</c>, the selected provider's sampling, and the off-spec
/// extras (<c>top_k</c>/<c>min_p</c>/... and the thinking template kwargs) land in the
/// right OpenAI wire shape. Assertions run over the SDK-serialized JSON (via
/// <see cref="ModelReaderWriter"/>) - i.e. the exact bytes that go on the wire. The
/// model id and <c>stream</c>/<c>stream_options</c> are injected by the SDK client at
/// call time and are asserted in <see cref="OpenAIChatModelClientTests"/>.
/// </summary>
public sealed class OpenAIChatRequestBuilderTests
{
    /// <summary>Empty provider sampling - the default for tests that don't exercise it.</summary>
    private static readonly ChatProviderParameters NoSampling = new();

    /// <summary>Defaulting wrapper so the bulk of the tests stay sampling-agnostic.</summary>
    private static (IReadOnlyList<ChatMessage> Messages, ChatCompletionOptions Options) Build(
        ChatCompletionRequest request, ChatProviderParameters? sampling = null) =>
        OpenAIChatRequestBuilder.Build(request, sampling ?? NoSampling);

    private static ChatCompletionRequest BaseRequest() => new()
    {
        ModelId = "ignored-port-id",
        Messages =
        [
            new ChatModelMessage { Role = "system", Content = "be terse" },
            new ChatModelMessage { Role = "user", Content = "hi" },
        ],
    };

    private static JsonNode OptionsJson(ChatCompletionOptions options) =>
        JsonNode.Parse(ModelReaderWriter.Write(options, ModelReaderWriterOptions.Json).ToString())!;

    private static JsonNode MessageJson(ChatMessage message) =>
        JsonNode.Parse(ModelReaderWriter.Write(message, ModelReaderWriterOptions.Json).ToString())!;

    private static JsonArray MessagesJson(IReadOnlyList<ChatMessage> messages) =>
        new(messages.Select(m => (JsonNode?)MessageJson(m)).ToArray());

    [Fact]
    public void Build_MapsRolesAndContent()
    {
        var (messages, _) = Build(BaseRequest());

        var json = MessagesJson(messages);
        json.Should().HaveCount(2);
        json[0]!["role"]!.GetValue<string>().Should().Be("system");
        json[0]!["content"]!.GetValue<string>().Should().Be("be terse");
        json[1]!["role"]!.GetValue<string>().Should().Be("user");
        json[1]!["content"]!.GetValue<string>().Should().Be("hi");
    }

    [Fact]
    public void Build_AdvertisesToolsAsFunctions_WithAutoToolChoice()
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

        var (_, options) = Build(request);

        var body = OptionsJson(options);
        var tools = body["tools"]!.AsArray();
        tools.Should().HaveCount(1);

        var fn = tools[0]!["function"]!;
        tools[0]!["type"]!.GetValue<string>().Should().Be("function");
        fn["name"]!.GetValue<string>().Should().Be("web_search");
        fn["description"]!.GetValue<string>().Should().Be("search the web");
        fn["parameters"]!["properties"]!["q"]!["type"]!.GetValue<string>().Should().Be("string");

        // The model decides whether to call - the spec default, stated explicitly.
        body["tool_choice"]!.GetValue<string>().Should().Be("auto");
    }

    [Fact]
    public void Build_MalformedToolSchema_DegradesToEmptyObject()
    {
        var request = BaseRequest() with
        {
            Tools = [new ChatToolSpec { Name = "t", Description = "d", ParametersSchema = "{not json" }],
        };

        var (_, options) = Build(request);
        var parameters = OptionsJson(options)["tools"]!.AsArray()[0]!["function"]!["parameters"]!;
        parameters["type"]!.GetValue<string>().Should().Be("object");
    }

    [Fact]
    public void Build_IncludesProviderSamplingWhenSet()
    {
        // Typed OpenAI-spec sampling from the provider; off-spec via Extra. MaxTokens
        // is the lone request-borne field (the runner's per-round budget cap).
        var sampling = new ChatProviderParameters
        {
            Temperature = 0.2,
            TopP = 0.9,
            PresencePenalty = 1.5,
            Seed = 42,
            Stop = ["END"],
            Extra = new()
            {
                ["top_k"] = "20",
                ["min_p"] = "0.05",
                ["repetition_penalty"] = "1.1",
            },
        };

        var (_, options) = Build(BaseRequest() with { MaxTokens = 128 }, sampling);

        var body = OptionsJson(options);
        body["temperature"]!.GetValue<double>().Should().BeApproximately(0.2, 1e-6);
        body["top_p"]!.GetValue<double>().Should().BeApproximately(0.9, 1e-6);
        body["presence_penalty"]!.GetValue<double>().Should().BeApproximately(1.5, 1e-6);
        body["max_completion_tokens"]!.GetValue<int>().Should().Be(128);
        body["seed"]!.GetValue<int>().Should().Be(42);
        body["stop"]!.AsArray()[0]!.GetValue<string>().Should().Be("END");

        // Off-spec extensions ride as root-level fields via JsonPatch, value-typed.
        body["top_k"]!.GetValue<int>().Should().Be(20);
        body["min_p"]!.GetValue<double>().Should().BeApproximately(0.05, 1e-6);
        body["repetition_penalty"]!.GetValue<double>().Should().BeApproximately(1.1, 1e-6);
    }

    [Fact]
    public void Build_OmitsUnsetSamplingParams()
    {
        // An empty provider sampling sends nothing - the upstream falls back to its
        // own generation_config defaults; never send zeros.
        var (_, options) = Build(BaseRequest());
        var body = OptionsJson(options).AsObject();
        body.ContainsKey("temperature").Should().BeFalse();
        body.ContainsKey("top_p").Should().BeFalse();
        body.ContainsKey("presence_penalty").Should().BeFalse();
        // The off-spec extensions are equally absent when the Extra map is empty.
        body.ContainsKey("top_k").Should().BeFalse();
        body.ContainsKey("min_p").Should().BeFalse();
        body.ContainsKey("repetition_penalty").Should().BeFalse();
    }

    [Fact]
    public void Build_SendsExplicitlySetZeroAndNeutralSamplingValues()
    {
        // A SET neutral value must ride the wire (distinct from unset, omitted above):
        // min_p 0 stays an integer 0, repetition_penalty 1.0 a float, presence 0.0 a
        // typed zero - exactly the qwen36 instruct/thinking presets, so guard against
        // a silent drop or a type slip (e.g. top_k becoming a float).
        var sampling = new ChatProviderParameters
        {
            PresencePenalty = 0.0,
            Extra = new()
            {
                ["top_k"] = "20",
                ["min_p"] = "0",
                ["repetition_penalty"] = "1.0",
            },
        };

        var body = OptionsJson(Build(BaseRequest(), sampling).Options);
        body["top_k"]!.GetValue<int>().Should().Be(20);
        body["min_p"]!.GetValue<double>().Should().Be(0.0);
        body["presence_penalty"]!.GetValue<double>().Should().Be(0.0);
        body["repetition_penalty"]!.GetValue<double>().Should().Be(1.0);
    }

    [Fact]
    public void Build_OmitsToolsAndToolChoiceWhenNone()
    {
        // Some servers reject tool_choice without tools - both must be absent.
        var (_, options) = Build(BaseRequest());
        var body = OptionsJson(options).AsObject();
        body.ContainsKey("tools").Should().BeFalse();
        body.ContainsKey("tool_choice").Should().BeFalse();
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

        var (messages, _) = Build(request);
        var json = MessageJson(messages[0]);
        json["role"]!.GetValue<string>().Should().Be("tool");
        json["tool_call_id"]!.GetValue<string>().Should().Be("call_9");
        json["content"]!.GetValue<string>().Should().Be("result");
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

        var (messages, _) = Build(request);
        var assistant = MessageJson(messages[0]).AsObject();

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
    public void Build_EmitsChatTemplateKwargsFromProviderExtra()
    {
        // The thinking template kwargs are off-spec, so they ride the provider's
        // Extra map (no dedicated request field). Absent extras -> no kwargs object.
        var (_, plain) = Build(BaseRequest());
        OptionsJson(plain).AsObject().ContainsKey("chat_template_kwargs").Should().BeFalse();

        var offSampling = new ChatProviderParameters
        {
            Extra = new() { ["chat_template_kwargs.enable_thinking"] = "false" },
        };
        var off = OptionsJson(Build(BaseRequest(), offSampling).Options);
        off["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>().Should().BeFalse();
        off["chat_template_kwargs"]!.AsObject().ContainsKey("preserve_thinking").Should().BeFalse();

        var bothSampling = new ChatProviderParameters
        {
            Extra = new()
            {
                ["chat_template_kwargs.enable_thinking"] = "true",
                ["chat_template_kwargs.preserve_thinking"] = "true",
            },
        };
        var both = OptionsJson(Build(BaseRequest(), bothSampling).Options);
        both["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>().Should().BeTrue();
        both["chat_template_kwargs"]!["preserve_thinking"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void Build_CarriesReasoningContentOnlyWhenPreserveThinkingOn_AndNeverEmpty()
    {
        var request = BaseRequest() with
        {
            Messages =
            [
                new ChatModelMessage { Role = "assistant", Content = "391", ReasoningContent = "17*23 = 391." },
                new ChatModelMessage { Role = "assistant", Content = "ok", ReasoningContent = string.Empty },
            ],
        };

        // Default provider (preserve_thinking off): prior reasoning is NOT replayed -
        // sending it to an instruct provider would inject phantom <think> blocks.
        var (plain, _) = Build(request);
        MessageJson(plain[0]).AsObject().ContainsKey("reasoning_content").Should().BeFalse();

        // preserve_thinking on: the non-empty reasoning rides; the empty one is still
        // omitted - empty <think> blocks drift the prompt (QwenLM/Qwen3.6#131).
        var preserve = new ChatProviderParameters
        {
            Extra = new() { ["chat_template_kwargs.preserve_thinking"] = "true" },
        };
        var (messages, _) = Build(request, preserve);
        MessageJson(messages[0])["reasoning_content"]!.GetValue<string>().Should().Be("17*23 = 391.");
        MessageJson(messages[1]).AsObject().ContainsKey("reasoning_content").Should().BeFalse();
    }

    [Fact]
    public void Build_EmitsContentArrayForUserMessageWithImages()
    {
        var request = BaseRequest() with
        {
            Messages =
            [
                new ChatModelMessage
                {
                    Role = "user",
                    Content = "what's in this picture?",
                    Images =
                    [
                        new ChatModelImage { MimeType = "image/png", DataBase64 = "aGVsbG8=" },
                        new ChatModelImage { MimeType = "image/jpeg", DataBase64 = "d29ybGQ=" },
                    ],
                },
            ],
        };

        var (messages, _) = Build(request);
        var parts = MessageJson(messages[0])["content"]!.AsArray();

        // Text part first, then one image_url part per image as a data URL.
        parts.Should().HaveCount(3);
        parts[0]!["type"]!.GetValue<string>().Should().Be("text");
        parts[0]!["text"]!.GetValue<string>().Should().Be("what's in this picture?");
        parts[1]!["type"]!.GetValue<string>().Should().Be("image_url");
        parts[1]!["image_url"]!["url"]!.GetValue<string>()
            .Should().Be("data:image/png;base64,aGVsbG8=");
        parts[2]!["image_url"]!["url"]!.GetValue<string>()
            .Should().Be("data:image/jpeg;base64,d29ybGQ=");
    }

    [Fact]
    public void Build_OmitsTextPartForImageOnlyMessage()
    {
        var request = BaseRequest() with
        {
            Messages =
            [
                new ChatModelMessage
                {
                    Role = "user",
                    Content = string.Empty,
                    Images = [new ChatModelImage { MimeType = "image/png", DataBase64 = "aGVsbG8=" }],
                },
            ],
        };

        var (messages, _) = Build(request);
        var parts = MessageJson(messages[0])["content"]!.AsArray();

        parts.Should().HaveCount(1);
        parts[0]!["type"]!.GetValue<string>().Should().Be("image_url");
    }

    [Fact]
    public void Build_KeepsPlainStringContentWithoutImages()
    {
        var (messages, _) = Build(BaseRequest());
        // No images -> content stays the plain string form (prefix-cache parity
        // with pre-vision conversations).
        MessageJson(messages[1])["content"]!.GetValueKind().Should().Be(System.Text.Json.JsonValueKind.String);
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

        var (messages, _) = Build(request);
        var assistant = MessageJson(messages[0]);
        assistant["content"]!.GetValue<string>().Should().Be("let me check");
        assistant["tool_calls"]!.AsArray().Should().HaveCount(1);
    }
}
