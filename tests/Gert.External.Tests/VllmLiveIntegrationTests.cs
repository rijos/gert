using FluentAssertions;
using Gert.External.Vllm;
using Gert.Service.Chat;
using Gert.Service.External;
using Gert.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gert.External.Tests;

/// <summary>
/// LIVE integration against a real vLLM server — skipped unless
/// <c>GERT_VLLM_URL</c> is set (CI has no GPU box). Drives the REAL wire path:
/// <see cref="VllmChatRequestBuilder"/> → HTTP SSE → <see cref="VllmStreamParser"/>,
/// then <see cref="ArtifactExtractor"/> over the assembled completion — proving
/// the model can be steered into named html/py/md fences and that our
/// request-shape (chat_template_kwargs, usage) holds against the deployed build.
///
/// Run:  GERT_VLLM_URL=http://192.168.10.99:8000 [GERT_VLLM_MODEL=qwen36] dotnet test \
///       --filter FullyQualifiedName~VllmLiveIntegration
/// </summary>
public sealed class VllmLiveIntegrationTests
{
    private static VllmChatModelClient? CreateClient(out string baseUrl)
    {
        baseUrl = Environment.GetEnvironmentVariable("GERT_VLLM_URL") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        // The client appends /v1/...; accept a configured url with or without it.
        baseUrl = baseUrl.TrimEnd('/');
        if (baseUrl.EndsWith("/v1", StringComparison.Ordinal))
        {
            baseUrl = baseUrl[..^3];
        }

        var options = new VllmOptions
        {
            BaseUrl = baseUrl,
            ChatModelId = Environment.GetEnvironmentVariable("GERT_VLLM_MODEL") ?? "qwen36",
            RequestTimeoutSeconds = 180,
        };

        var http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds),
        };

        return new VllmChatModelClient(
            http, Options.Create(options), NullLogger<VllmChatModelClient>.Instance);
    }

    [Fact]
    public async Task Live_model_produces_named_html_py_and_md_artifacts()
    {
        var client = CreateClient(out var baseUrl);
        Assert.SkipWhen(client is null, "GERT_VLLM_URL is not set — live vLLM integration skipped.");

        var request = new ChatCompletionRequest
        {
            ModelId = "default", // → VllmOptions.ChatModelId
            Messages =
            [
                new ChatModelMessage
                {
                    Role = "user",
                    Content =
                        "Reply with exactly three fenced code blocks and no other code blocks. " +
                        "Each fence's info string must carry a name= token exactly as shown:\n" +
                        "1. ```html name=demo.html``` — a minimal page with an <h1>\n" +
                        "2. ```python name=fib.py``` — a fibonacci function\n" +
                        "3. ```md name=notes.md``` — a one-line note\n" +
                        "Keep each block under 10 lines.",
                },
            ],
            // Deterministic-ish + fast: no sampling spread, no thinking detour.
            Temperature = 0,
            MaxTokens = 1200,
            EnableThinking = false,
        };

        var content = new System.Text.StringBuilder();
        var reasoning = new System.Text.StringBuilder();
        int? completionTokens = null;
        int? promptTokens = null;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(170));
        await foreach (var chunk in client!.StreamAsync(request, cts.Token))
        {
            content.Append(chunk.TextDelta);
            reasoning.Append(chunk.ReasoningDelta);
            completionTokens ??= chunk.TokenCount;
            completionTokens = chunk.TokenCount ?? completionTokens;
            promptTokens = chunk.PromptTokenCount ?? promptTokens;
        }

        // The real wire path delivered text and the trailing usage chunk
        // (prompt tokens are what the composer's context ring runs on).
        content.Length.Should().BeGreaterThan(0, $"the model at {baseUrl} should reply");
        completionTokens.Should().NotBeNull("stream_options.include_usage is always requested");
        promptTokens.Should().BeGreaterThan(0, "vLLM reports prompt_tokens on the usage tail");

        // enable_thinking=false reached the template: no reasoning streamed.
        reasoning.Length.Should().Be(0, "chat_template_kwargs.enable_thinking=false suppresses thinking");

        // The extractor lifts all three kinds out of the real completion.
        var artifacts = ArtifactExtractor.Extract(content.ToString());
        artifacts.Select(a => a.Kind).Should().Contain([ArtifactKind.Html, ArtifactKind.Py, ArtifactKind.Md]);
        artifacts.Should().ContainSingle(a => a.Name == "demo.html").Which.Content.Should().Contain("<h1");
        artifacts.Should().ContainSingle(a => a.Name == "fib.py").Which.Content.Should().Contain("def ");
        artifacts.Should().ContainSingle(a => a.Name == "notes.md");
    }

    [Fact]
    public async Task Live_model_streams_reasoning_when_thinking_is_on()
    {
        var client = CreateClient(out _);
        Assert.SkipWhen(client is null, "GERT_VLLM_URL is not set — live vLLM integration skipped.");

        var request = new ChatCompletionRequest
        {
            ModelId = "default",
            Messages =
            [
                new ChatModelMessage { Role = "user", Content = "What is 17 * 23? Answer briefly." },
            ],
            Temperature = 0,
            MaxTokens = 700,
            EnableThinking = true,
        };

        var content = new System.Text.StringBuilder();
        var reasoning = new System.Text.StringBuilder();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(170));
        await foreach (var chunk in client!.StreamAsync(request, cts.Token))
        {
            content.Append(chunk.TextDelta);
            reasoning.Append(chunk.ReasoningDelta);
        }

        // --reasoning-parser qwen3 splits thinking from the answer; both flow
        // through our parser into their own delta lanes.
        reasoning.Length.Should().BeGreaterThan(0, "thinking-on must surface reasoning_content");
        content.ToString().Should().Contain("391");
    }
}
