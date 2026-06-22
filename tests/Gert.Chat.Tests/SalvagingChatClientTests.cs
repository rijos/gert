using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Gert.Chat.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Gert.Chat.Tests;

/// <summary>
/// Drives the real <see cref="SalvagingChatClient"/> over the M.E.AI OpenAI adapter through a
/// stubbed <see cref="HttpMessageHandler"/> - no live server. Two halves:
/// <list type="bullet">
/// <item><b>Request shaping</b> (the wire bytes the old request builder owned, now the adapter +
/// SalvagingChatClient): roles/content, tools + auto tool-choice, the provider's typed sampling and
/// off-spec <c>Extra</c> JsonPatch (top_k / chat_template_kwargs), the reasoning_content replay gate,
/// assistant tool calls, tool-result messages, and vision image parts.</item>
/// <item><b>Response</b>: a canned SSE stream parses end-to-end into <see cref="ChatResponseUpdate"/>s
/// (proving the salvage parser runs over the adapter's raw representation), and an upstream error
/// keeps the port's <see cref="HttpRequestException"/> contract with the server's diagnostic.</item>
/// </list>
/// </summary>
public sealed class SalvagingChatClientTests
{
    private static (SalvagingChatClient Client, StubHttpMessageHandler Handler) NewClient(
        ChatProviderParameters? parameters = null,
        string sse = "data: [DONE]\n\n",
        HttpStatusCode status = HttpStatusCode.OK)
    {
        // A NON-seekable body, like a real network response (the SDK transport trips over a
        // seekable, consumed stream at dispose).
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            var content = new StreamContent(new NonSeekableReadStream(Encoding.UTF8.GetBytes(sse)));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return new HttpResponseMessage(status) { Content = content };
        });

        var http = new HttpClient(handler);
        var p = parameters ?? new ChatProviderParameters { BaseUrl = "http://openai.test:8000", Model = "qwen-test" };
        var inner = OpenAISdkClient.CreateSdkClient(http, p.BaseUrl, p.ApiKey).GetChatClient(p.Model).AsIChatClient();
        return (new SalvagingChatClient(inner, p, NullLogger<SalvagingChatClient>.Instance), handler);
    }

    private static ChatProviderParameters Params(Action<ChatProviderParameters>? configure = null)
    {
        var p = new ChatProviderParameters { BaseUrl = "http://openai.test:8000", Model = "qwen-test" };
        configure?.Invoke(p);
        return p;
    }

    private static async Task<JsonNode> WireBodyAsync(
        SalvagingChatClient client, StubHttpMessageHandler handler, IEnumerable<ChatMessage> messages, ChatOptions? options = null)
    {
        await foreach (var _ in client.GetStreamingResponseAsync(messages, options))
        {
            // drain - the request is sent on enumeration
        }

        return JsonNode.Parse(handler.LastRequestBody!)!;
    }

    private static AITool Tool(string name, string description, string schema) =>
        AIFunctionFactory.CreateDeclaration(name, description, JsonDocument.Parse(schema).RootElement);

    // ---- request shape basics ----

    [Fact]
    public async Task SendsCorrectlyShapedRequest()
    {
        var (client, handler) = NewClient();

        var body = await WireBodyAsync(client, handler, [new ChatMessage(ChatRole.User, "hi")]);

        handler.LastRequestUri!.AbsolutePath.Should().Be("/v1/chat/completions");
        body["model"]!.GetValue<string>().Should().Be("qwen-test");
        body["stream"]!.GetValue<bool>().Should().BeTrue();
        body["stream_options"]!["include_usage"]!.GetValue<bool>().Should().BeTrue();
        body["messages"]!.AsArray()[0]!["content"]!.GetValue<string>().Should().Be("hi");
    }

    [Fact]
    public async Task MapsRolesAndContent()
    {
        var (client, handler) = NewClient();

        var body = await WireBodyAsync(
            client, handler, [new ChatMessage(ChatRole.System, "be terse"), new ChatMessage(ChatRole.User, "hi")]);

        var msgs = body["messages"]!.AsArray();
        msgs[0]!["role"]!.GetValue<string>().Should().Be("system");
        msgs[0]!["content"]!.GetValue<string>().Should().Be("be terse");
        msgs[1]!["role"]!.GetValue<string>().Should().Be("user");
        msgs[1]!["content"]!.GetValue<string>().Should().Be("hi");
    }

    [Fact]
    public async Task AdvertisesToolsAsFunctions_WithAutoToolChoice()
    {
        var (client, handler) = NewClient();
        var options = new ChatOptions
        {
            Tools = [Tool("web_search", "search the web", """{"type":"object","properties":{"q":{"type":"string"}}}""")],
        };

        var body = await WireBodyAsync(client, handler, [new ChatMessage(ChatRole.User, "hi")], options);

        var tools = body["tools"]!.AsArray();
        tools.Should().HaveCount(1);
        tools[0]!["type"]!.GetValue<string>().Should().Be("function");
        tools[0]!["function"]!["name"]!.GetValue<string>().Should().Be("web_search");
        tools[0]!["function"]!["description"]!.GetValue<string>().Should().Be("search the web");
        tools[0]!["function"]!["parameters"]!["properties"]!["q"]!["type"]!.GetValue<string>().Should().Be("string");
        // The model decides whether to call - the spec default, stated explicitly.
        body["tool_choice"]!.GetValue<string>().Should().Be("auto");
    }

    [Fact]
    public async Task OmitsToolsAndToolChoiceWhenNone()
    {
        var (client, handler) = NewClient();

        var body = (await WireBodyAsync(client, handler, [new ChatMessage(ChatRole.User, "hi")])).AsObject();

        body.ContainsKey("tools").Should().BeFalse();
        body.ContainsKey("tool_choice").Should().BeFalse();
    }

    [Fact]
    public async Task IncludesProviderSamplingWhenSet()
    {
        var parameters = Params(p =>
        {
            p.Temperature = 0.2;
            p.TopP = 0.9;
            p.PresencePenalty = 1.5;
            p.Seed = 42;
            p.Stop = ["END"];
            p.Extra = new() { ["top_k"] = "20", ["min_p"] = "0.05", ["repetition_penalty"] = "1.1" };
        });
        var (client, handler) = NewClient(parameters);

        // MaxOutputTokens is the lone request-borne field (the runner's per-round budget cap).
        var body = await WireBodyAsync(
            client, handler, [new ChatMessage(ChatRole.User, "hi")], new ChatOptions { MaxOutputTokens = 128 });

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
    public async Task OmitsUnsetSamplingParams()
    {
        var (client, handler) = NewClient();

        var body = (await WireBodyAsync(client, handler, [new ChatMessage(ChatRole.User, "hi")])).AsObject();

        body.ContainsKey("temperature").Should().BeFalse();
        body.ContainsKey("top_p").Should().BeFalse();
        body.ContainsKey("presence_penalty").Should().BeFalse();
        body.ContainsKey("top_k").Should().BeFalse();
        body.ContainsKey("min_p").Should().BeFalse();
        body.ContainsKey("repetition_penalty").Should().BeFalse();
    }

    [Fact]
    public async Task SendsExplicitlySetZeroAndNeutralSamplingValues()
    {
        // A SET neutral value must ride the wire (distinct from unset): min_p 0 an integer,
        // repetition_penalty 1.0 a float, presence 0.0 a typed zero - the qwen36 presets.
        var parameters = Params(p =>
        {
            p.PresencePenalty = 0.0;
            p.Extra = new() { ["top_k"] = "20", ["min_p"] = "0", ["repetition_penalty"] = "1.0" };
        });
        var (client, handler) = NewClient(parameters);

        var body = await WireBodyAsync(client, handler, [new ChatMessage(ChatRole.User, "hi")]);

        body["top_k"]!.GetValue<int>().Should().Be(20);
        body["min_p"]!.GetValue<double>().Should().Be(0.0);
        body["presence_penalty"]!.GetValue<double>().Should().Be(0.0);
        body["repetition_penalty"]!.GetValue<double>().Should().Be(1.0);
    }

    [Fact]
    public async Task EmitsChatTemplateKwargsFromProviderExtra()
    {
        var (plainClient, plainHandler) = NewClient();
        (await WireBodyAsync(plainClient, plainHandler, [new ChatMessage(ChatRole.User, "hi")]))
            .AsObject().ContainsKey("chat_template_kwargs").Should().BeFalse();

        var (offClient, offHandler) = NewClient(
            Params(p => p.Extra = new() { ["chat_template_kwargs.enable_thinking"] = "false" }));
        var off = await WireBodyAsync(offClient, offHandler, [new ChatMessage(ChatRole.User, "hi")]);
        off["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>().Should().BeFalse();
        off["chat_template_kwargs"]!.AsObject().ContainsKey("preserve_thinking").Should().BeFalse();

        var (bothClient, bothHandler) = NewClient(Params(p => p.Extra = new()
        {
            ["chat_template_kwargs.enable_thinking"] = "true",
            ["chat_template_kwargs.preserve_thinking"] = "true",
        }));
        var both = await WireBodyAsync(bothClient, bothHandler, [new ChatMessage(ChatRole.User, "hi")]);
        both["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>().Should().BeTrue();
        both["chat_template_kwargs"]!["preserve_thinking"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task CarriesReasoningContentOnlyWhenPreserveThinkingOn_AndNeverEmpty()
    {
        var history = new ChatMessage[]
        {
            new(ChatRole.Assistant, [new TextReasoningContent("17*23 = 391."), new TextContent("391")]),
            new(ChatRole.Assistant, [new TextReasoningContent(string.Empty), new TextContent("ok")]),
            new(ChatRole.User, "next"),
        };

        // Default provider (preserve_thinking off): prior reasoning is NOT replayed - it would inject
        // phantom <think> blocks into an instruct provider.
        var (plainClient, plainHandler) = NewClient();
        var plain = await WireBodyAsync(plainClient, plainHandler, history);
        plain["messages"]!.AsArray()[0]!.AsObject().ContainsKey("reasoning_content").Should().BeFalse();

        // preserve_thinking on: the non-empty reasoning rides; the empty one is still omitted - empty
        // <think> blocks drift the prompt (QwenLM/Qwen3.6#131).
        var (onClient, onHandler) = NewClient(
            Params(p => p.Extra = new() { ["chat_template_kwargs.preserve_thinking"] = "true" }));
        var on = await WireBodyAsync(onClient, onHandler, history);
        var msgs = on["messages"]!.AsArray();
        msgs[0]!["reasoning_content"]!.GetValue<string>().Should().Be("17*23 = 391.");
        msgs[1]!.AsObject().ContainsKey("reasoning_content").Should().BeFalse();
    }

    [Fact]
    public async Task CarriesToolCallIdOnToolMessages()
    {
        var (client, handler) = NewClient();

        var body = await WireBodyAsync(
            client, handler, [new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_9", "result")])]);

        var msg = body["messages"]!.AsArray()[0]!;
        msg["role"]!.GetValue<string>().Should().Be("tool");
        msg["tool_call_id"]!.GetValue<string>().Should().Be("call_9");
        msg["content"]!.GetValue<string>().Should().Be("result");
    }

    [Fact]
    public async Task SerializesAssistantToolCallsPerOpenAiWireFormat()
    {
        var (client, handler) = NewClient();
        var assistant = new ChatMessage(ChatRole.Assistant, [
            new FunctionCallContent("call_1", "web_search", new Dictionary<string, object?> { ["query"] = "x" }),
            new FunctionCallContent("call_2", "get_datetime", new Dictionary<string, object?>()),
        ]);

        var body = await WireBodyAsync(client, handler, [assistant]);
        var msg = body["messages"]!.AsArray()[0]!.AsObject();

        // Tool-call-only turn: no content/tool_call_id; tool_calls in call order; `arguments` is the
        // raw JSON string (the adapter may pretty-print it, so parse rather than byte-compare).
        msg.ContainsKey("content").Should().BeFalse();
        msg.ContainsKey("tool_call_id").Should().BeFalse();
        var calls = msg["tool_calls"]!.AsArray();
        calls.Should().HaveCount(2);
        calls[0]!["id"]!.GetValue<string>().Should().Be("call_1");
        calls[0]!["type"]!.GetValue<string>().Should().Be("function");
        calls[0]!["function"]!["name"]!.GetValue<string>().Should().Be("web_search");
        JsonNode.Parse(calls[0]!["function"]!["arguments"]!.GetValue<string>())!["query"]!.GetValue<string>()
            .Should().Be("x");
        calls[1]!["function"]!["name"]!.GetValue<string>().Should().Be("get_datetime");
    }

    [Fact]
    public async Task KeepsContentOnAssistantToolCallTurnWhenPresent()
    {
        var (client, handler) = NewClient();
        var assistant = new ChatMessage(ChatRole.Assistant, [
            new TextContent("let me check"),
            new FunctionCallContent("c", "web_search", new Dictionary<string, object?>()),
        ]);

        var body = await WireBodyAsync(client, handler, [assistant]);
        var msg = body["messages"]!.AsArray()[0]!;

        msg["content"]!.GetValue<string>().Should().Be("let me check");
        msg["tool_calls"]!.AsArray().Should().HaveCount(1);
    }

    [Fact]
    public async Task EmitsContentArrayForUserMessageWithImages()
    {
        var (client, handler) = NewClient();
        var user = new ChatMessage(ChatRole.User, [
            new TextContent("what's in this picture?"),
            new DataContent(Convert.FromBase64String("aGVsbG8="), "image/png"),
            new DataContent(Convert.FromBase64String("d29ybGQ="), "image/jpeg"),
        ]);

        var body = await WireBodyAsync(client, handler, [user]);
        var parts = body["messages"]!.AsArray()[0]!["content"]!.AsArray();

        parts.Should().HaveCount(3);
        parts[0]!["type"]!.GetValue<string>().Should().Be("text");
        parts[0]!["text"]!.GetValue<string>().Should().Be("what's in this picture?");
        parts[1]!["type"]!.GetValue<string>().Should().Be("image_url");
        parts[1]!["image_url"]!["url"]!.GetValue<string>().Should().Be("data:image/png;base64,aGVsbG8=");
        parts[2]!["image_url"]!["url"]!.GetValue<string>().Should().Be("data:image/jpeg;base64,d29ybGQ=");
    }

    [Fact]
    public async Task KeepsPlainStringContentWithoutImages()
    {
        var (client, handler) = NewClient();

        var body = await WireBodyAsync(client, handler, [new ChatMessage(ChatRole.User, "hi")]);

        body["messages"]!.AsArray()[0]!["content"]!.GetValueKind().Should().Be(JsonValueKind.String);
    }

    // ---- response: the salvage parser runs end-to-end over the adapter's raw updates ----

    [Fact]
    public async Task ParsesCannedSseIntoUpdateSequence()
    {
        var sse = string.Concat(
            """data: {"id":"c1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"Hi"},"finish_reason":null}]}""" + "\n\n",
            """data: {"id":"c1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":" there"},"finish_reason":null}]}""" + "\n\n",
            """data: {"id":"c1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""" + "\n\n",
            "data: [DONE]\n\n");
        var (client, _) = NewClient(sse: sse);

        var text = new StringBuilder();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
        {
            foreach (var part in update.Contents.OfType<TextContent>())
            {
                text.Append(part.Text);
            }
        }

        text.ToString().Should().Be("Hi there");
    }

    [Fact]
    public async Task ParsesToolCallStream()
    {
        var sse = string.Concat(
            """data: {"id":"c1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"run_python","arguments":"{\"code\":\"1+1\"}"}}]},"finish_reason":null}]}""" + "\n\n",
            """data: {"id":"c1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""" + "\n\n",
            "data: [DONE]\n\n");
        var (client, _) = NewClient(sse: sse);

        var calls = new List<FunctionCallContent>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
        {
            calls.AddRange(update.Contents.OfType<FunctionCallContent>().Where(c => c.Arguments is not null));
        }

        var call = calls.Should().ContainSingle().Subject;
        call.Name.Should().Be("run_python");
        // The parsed argument value round-trips (default STJ escapes '+' to +, so check the value).
        ((JsonElement)call.Arguments!["code"]!).GetString().Should().Be("1+1");
    }

    [Fact]
    public async Task UpstreamError_SurfacesAsHttpRequestExceptionWithDetail()
    {
        var (client, _) = NewClient(
            sse: """{"error":{"message":"chat template blew up"}}""",
            status: HttpStatusCode.BadRequest);

        var act = async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
            {
                // drain
            }
        };

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.Message.Should().Contain("400").And.Contain("chat template");
    }

    /// <summary>A read-only stream that, like a network stream, cannot seek.</summary>
    private sealed class NonSeekableReadStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes, writable: false);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
