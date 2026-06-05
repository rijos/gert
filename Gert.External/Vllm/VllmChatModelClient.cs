using System.Runtime.CompilerServices;
using System.Text;
using Gert.Service.External;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gert.External.Vllm;

/// <summary>
/// Real <see cref="IChatModelClient"/> over a vLLM OpenAI-compatible
/// <c>/v1/chat/completions</c> endpoint (tech-stack.md § Model API). Streams the SSE
/// response, parsing each <c>data:</c> line via the pure <see cref="VllmStreamParser"/>
/// into <see cref="ChatModelChunk"/>s (deltas, tool calls, finish, usage).
///
/// <para>
/// The typed <c>HttpClient</c> is configured by <see cref="ServiceCollectionExtensions"/>
/// with the base URL, bearer key (secret, F8), and a Polly timeout+retry pipeline.
/// </para>
///
/// <para>
/// <b>Integration-only:</b> the live wire path (a real vLLM server) cannot run in CI;
/// it is exercised by U13 (Python mock upstreams) + staging. The unit tests drive this
/// client through a stubbed <c>HttpMessageHandler</c> serving a canned SSE stream, and
/// test <see cref="VllmChatRequestBuilder"/> / <see cref="VllmStreamParser"/> directly.
/// </para>
/// </summary>
public sealed class VllmChatModelClient : IChatModelClient
{
    /// <summary>The named <c>HttpClient</c> for the vLLM upstream.</summary>
    public const string HttpClientName = "vllm";

    private readonly HttpClient _http;
    private readonly VllmOptions _options;
    private readonly ILogger<VllmChatModelClient> _logger;

    /// <summary>Construct over the configured typed client + options.</summary>
    public VllmChatModelClient(
        HttpClient http,
        IOptions<VllmOptions> options,
        ILogger<VllmChatModelClient> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatModelChunk> StreamAsync(
        ChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Honor the per-request model from the picker; the "default" sentinel
        // (and an unset id) means the operator-configured chat model.
        var modelId = string.IsNullOrWhiteSpace(request.ModelId) || request.ModelId == "default"
            ? _options.ChatModelId
            : request.ModelId;
        var body = VllmChatRequestBuilder.Build(request, modelId);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(
                body.ToJsonString(),
                Encoding.UTF8,
                "application/json"),
        };

        // Stream: read headers first, then the body incrementally.
        using var response = await _http
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "vLLM chat completion failed with status {Status}.", (int)response.StatusCode);
            response.EnsureSuccessStatusCode();
        }

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var parser = new VllmStreamParser();

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (line.Length == 0 || !line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line["data:".Length..].Trim();
            if (payload == "[DONE]")
            {
                break;
            }

            foreach (var chunk in parser.Parse(payload))
            {
                yield return chunk;
            }
        }

        // Surface a terminal finish chunk if the server closed without one inline.
        foreach (var chunk in parser.Flush())
        {
            yield return chunk;
        }
    }
}
