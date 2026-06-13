using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Gert.External.OpenAI;
using Gert.External.Providers;
using Gert.Service.External;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gert.External.Tests;

/// <summary>
/// <see cref="OpenAIWireLogger"/> - the Debug-only request/response wire trace. Proves
/// the actual POST body + headers are logged for tuning, the bearer credential is
/// redacted (F8), and the whole thing is silent (and transparent) above Debug.
/// </summary>
public sealed class OpenAIWireLoggerTests
{
    private static HttpClient ClientOver(OpenAIWireLogger logger, HttpStatusCode status = HttpStatusCode.OK)
    {
        var inner = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(status)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
        logger.InnerHandler = inner;
        return new HttpClient(logger);
    }

    private static HttpRequestMessage ChatPost(string body) =>
        new(HttpMethod.Post, "http://vllm.test/v1/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", "sk-super-secret") },
        };

    [Fact]
    public async Task Logs_the_post_body_and_headers_with_the_bearer_redacted_at_debug()
    {
        var log = new CapturingLogger<OpenAIWireLogger>();
        using var client = ClientOver(new OpenAIWireLogger(log));

        const string body = """{"model":"qwen36","messages":[{"role":"user","content":"hi"}],"top_k":20}""";
        var response = await client.SendAsync(ChatPost(body));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = string.Join("\n", log.Messages);

        // The actual request shape an operator tunes against.
        text.Should().Contain("chat/completions");
        text.Should().Contain("\"top_k\":20", "the real post body rides into the trace");
        text.Should().Contain("\"model\":\"qwen36\"");
        // The response side is traced too (status + headers, not the SSE body).
        text.Should().Contain("OpenAI response: 200");

        // F8: the credential is redacted, never logged.
        text.Should().Contain("Authorization=<redacted>");
        text.Should().NotContain("sk-super-secret");
    }

    [Fact]
    public async Task Fires_on_a_real_sdk_chat_request_through_the_handler_chain()
    {
        // The actually-uncertain link: does the SDK's HttpClientPipelineTransport run
        // our DelegatingHandler? Build the client EXACTLY as ServiceCollectionExtensions
        // does - an HttpClient whose chain is [OpenAIWireLogger -> SSE stub] - and drive a
        // real CompleteChatStreamingAsync. The trace must carry the SDK-serialized body.
        var log = new CapturingLogger<OpenAIWireLogger>();
        var sse = new StubHttpMessageHandler((_, _) =>
        {
            var content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes("data: [DONE]\n\n")));
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var wire = new OpenAIWireLogger(log) { InnerHandler = sse };
        using var http = new HttpClient(wire) { BaseAddress = new Uri("http://vllm.test") };
        var client = new OpenAIChatModelClient(
            http,
            new ChatProviderParameters { BaseUrl = "http://vllm.test", Model = "qwen36" },
            NullLogger<OpenAIChatModelClient>.Instance);

        var request = new ChatCompletionRequest
        {
            ModelId = "default",
            Messages = [new ChatModelMessage { Role = "user", Content = "hi" }],
        };
        await foreach (var _ in client.StreamAsync(request, CancellationToken.None))
        {
            // drain
        }

        var text = string.Join("\n", log.Messages);
        text.Should().Contain("OpenAI request:");
        text.Should().Contain("chat/completions");
        // The SDK injects the resolved model into the serialized body - proof the trace
        // is the real wire request, not the pre-SDK ChatCompletionOptions.
        text.Should().Contain("\"model\":\"qwen36\"");
    }

    [Fact]
    public async Task Is_silent_and_passes_through_when_debug_is_off()
    {
        var log = new CapturingLogger<OpenAIWireLogger> { Enabled = false };
        using var client = ClientOver(new OpenAIWireLogger(log));

        var response = await client.SendAsync(ChatPost("""{"model":"qwen36"}"""));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the request still completes");
        log.Messages.Should().BeEmpty("nothing is logged above Debug");
    }

    /// <summary>A minimal in-memory <see cref="ILogger{T}"/> that records rendered messages.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public bool Enabled { get; init; } = true;

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => Enabled;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
