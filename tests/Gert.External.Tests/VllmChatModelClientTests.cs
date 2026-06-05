using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Gert.External.Vllm;
using Gert.Service.External;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gert.External.Tests;

/// <summary>
/// Drives the real <see cref="VllmChatModelClient"/> through a stubbed
/// <see cref="HttpMessageHandler"/>: asserts the request body shape (model, messages,
/// stream) and that a canned SSE stream parses into the expected chunk sequence (deltas
/// → tool call → finish). No vLLM server.
/// </summary>
public sealed class VllmChatModelClientTests
{
    private static (VllmChatModelClient Client, StubHttpMessageHandler Handler) NewClient(string sse)
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        });

        var http = new HttpClient(handler) { BaseAddress = new Uri("http://vllm.test") };
        var options = Options.Create(new VllmOptions { ChatModelId = "qwen-test" });
        return (new VllmChatModelClient(http, options, NullLogger<VllmChatModelClient>.Instance), handler);
    }

    private static ChatCompletionRequest Request(string modelId = "default") => new()
    {
        ModelId = modelId,
        Messages = [new ChatModelMessage { Role = "user", Content = "hello" }],
    };

    [Fact]
    public async Task StreamAsync_SendsCorrectlyShapedRequest()
    {
        var (client, handler) = NewClient("data: [DONE]\n\n");

        // the "default" sentinel resolves to the configured chat model
        await foreach (var _ in client.StreamAsync(Request()))
        {
            // drain
        }

        handler.LastRequestUri!.AbsolutePath.Should().Be("/v1/chat/completions");
        var body = JsonNode.Parse(handler.LastRequestBody!)!;
        body["model"]!.GetValue<string>().Should().Be("qwen-test");
        body["stream"]!.GetValue<bool>().Should().BeTrue();
        body["messages"]!.AsArray()[0]!["content"]!.GetValue<string>().Should().Be("hello");
    }

    [Fact]
    public async Task StreamAsync_HonorsPerRequestModelId()
    {
        var (client, handler) = NewClient("data: [DONE]\n\n");

        // a concrete id from the catalog/picker overrides the configured default
        await foreach (var _ in client.StreamAsync(Request("llama-3.3-70b-instruct")))
        {
            // drain
        }

        var body = JsonNode.Parse(handler.LastRequestBody!)!;
        body["model"]!.GetValue<string>().Should().Be("llama-3.3-70b-instruct");
    }

    [Fact]
    public async Task StreamAsync_ParsesCannedSseIntoChunkSequence()
    {
        var sse = string.Join(
            "\n",
            """data: {"choices":[{"delta":{"content":"Hi"},"finish_reason":null}]}""",
            "",
            """data: {"choices":[{"delta":{"content":" there"},"finish_reason":null}]}""",
            "",
            """data: {"choices":[{"delta":{},"finish_reason":"stop"}]}""",
            "",
            "data: [DONE]",
            "");

        var (client, _) = NewClient(sse);

        var chunks = new List<ChatModelChunk>();
        await foreach (var chunk in client.StreamAsync(Request()))
        {
            chunks.Add(chunk);
        }

        chunks.Should().HaveCount(3);
        chunks[0].TextDelta.Should().Be("Hi");
        chunks[1].TextDelta.Should().Be(" there");
        chunks[2].FinishReason.Should().Be("stop");
    }

    [Fact]
    public async Task StreamAsync_ParsesToolCallStream()
    {
        var sse = string.Join(
            "\n",
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"run_python","arguments":"{\"code\":\"1+1\"}"}}]},"finish_reason":null}]}""",
            "",
            """data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""",
            "",
            "data: [DONE]",
            "");

        var (client, _) = NewClient(sse);

        var chunks = new List<ChatModelChunk>();
        await foreach (var chunk in client.StreamAsync(Request()))
        {
            chunks.Add(chunk);
        }

        var tool = chunks.Single(c => c.ToolCall is not null).ToolCall!;
        tool.Name.Should().Be("run_python");
        tool.ArgumentsJson.Should().Be("""{"code":"1+1"}""");
        chunks.Single(c => c.FinishReason is not null).FinishReason.Should().Be("tool_calls");
    }
}
