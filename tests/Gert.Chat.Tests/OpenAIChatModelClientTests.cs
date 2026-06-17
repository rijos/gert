using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Gert.Chat.OpenAI;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Service.External;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gert.Chat.Tests;

/// <summary>
/// Drives the real <see cref="OpenAIChatModelClient"/> through a stubbed
/// <see cref="HttpMessageHandler"/>: asserts the request body shape (model, messages,
/// stream + stream_options) and that a canned SSE stream parses into the expected chunk
/// sequence (deltas -> tool call -> finish). No live server.
/// </summary>
public sealed class OpenAIChatModelClientTests
{
    private static (OpenAIChatModelClient Client, StubHttpMessageHandler Handler) NewClient(
        string sse, HttpStatusCode status = HttpStatusCode.OK)
    {
        // The body rides a NON-seekable stream, like a real network response: the
        // SDK transport treats seekable streams as bufferable and trips over the
        // consumed position at dispose ("Content stream position is not at
        // beginning of stream").
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            var content = new StreamContent(new NonSeekableReadStream(Encoding.UTF8.GetBytes(sse)));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return new HttpResponseMessage(status) { Content = content };
        });

        var http = new HttpClient(handler);
        var parameters = new ChatProviderParameters
        {
            BaseUrl = "http://openai.test:8000",
            Model = "qwen-test",
        };
        return (new OpenAIChatModelClient(http, parameters, NullLogger<OpenAIChatModelClient>.Instance), handler);
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

        // the provider fixes the upstream model id
        await foreach (var _ in client.StreamAsync(Request()))
        {
            // drain
        }

        handler.LastRequestUri!.AbsolutePath.Should().Be("/v1/chat/completions");
        var body = JsonNode.Parse(handler.LastRequestBody!)!;
        body["model"]!.GetValue<string>().Should().Be("qwen-test");
        body["stream"]!.GetValue<bool>().Should().BeTrue();
        // Usage must ride the final SSE chunk (turn accounting).
        body["stream_options"]!["include_usage"]!.GetValue<bool>().Should().BeTrue();
        body["messages"]!.AsArray()[0]!["content"]!.GetValue<string>().Should().Be("hello");
    }

    [Fact]
    public async Task StreamAsync_UsesTheProvidersModel_NotTheRequestProviderSlug()
    {
        var (client, handler) = NewClient("data: [DONE]\n\n");

        // request.ModelId is the provider SLUG (it already picked this client); the
        // upstream model is the provider's own Model, so the slug never reaches the wire.
        await foreach (var _ in client.StreamAsync(Request("qwen36-thinking")))
        {
            // drain
        }

        var body = JsonNode.Parse(handler.LastRequestBody!)!;
        body["model"]!.GetValue<string>().Should().Be("qwen-test");
    }

    [Fact]
    public async Task StreamAsync_ParsesCannedSseIntoChunkSequence()
    {
        var sse = string.Join(
            "\n",
            """data: {"id":"c1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"Hi"},"finish_reason":null}]}""",
            string.Empty,
            """data: {"id":"c1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":" there"},"finish_reason":null}]}""",
            string.Empty,
            """data: {"id":"c1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
            string.Empty,
            "data: [DONE]",
            string.Empty);

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
            """data: {"id":"c1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"run_python","arguments":"{\"code\":\"1+1\"}"}}]},"finish_reason":null}]}""",
            string.Empty,
            """data: {"id":"c1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""",
            string.Empty,
            "data: [DONE]",
            string.Empty);

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

    [Fact]
    public async Task StreamAsync_UpstreamError_SurfacesAsHttpRequestExceptionWithDetail()
    {
        // The port's error contract: upstream failures keep arriving as
        // HttpRequestException, with the server's diagnostic in the message.
        var (client, _) = NewClient(
            """{"error":{"message":"chat template blew up"}}""",
            HttpStatusCode.BadRequest);

        var act = async () =>
        {
            await foreach (var _ in client.StreamAsync(Request()))
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
