using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Runtime.CompilerServices;
using Gert.External.Providers;
using Gert.Service.External;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;

namespace Gert.External.OpenAI;

/// <summary>
/// Real <see cref="IChatModelClient"/> over an OpenAI-compatible
/// <c>/v1/chat/completions</c> endpoint - vLLM in the reference deployment
/// (tech-stack.md section Model API). Built on the official OpenAI SDK: the SDK owns the wire
/// format and SSE parsing; <see cref="OpenAIChatRequestBuilder"/> maps the port DTO in,
/// and the pure <see cref="OpenAIStreamParser"/> maps streamed updates out into
/// <see cref="ChatModelChunk"/>s (deltas, tool calls, finish, usage).
///
/// <para>
/// <b>Timeout ownership is unchanged:</b> the SDK pipeline rides the typed
/// <c>HttpClient</c> configured by <see cref="ServiceCollectionExtensions"/> (base URL,
/// bearer key (secret, F8), an <b>infinite</b> client timeout, and a Polly pipeline that
/// bounds only the pre-stream phase, per
/// <see cref="OpenAIOptions.RequestTimeoutSeconds"/>/<see cref="OpenAIOptions.RetryCount"/>).
/// The SDK's own retry policy and network timeout are disabled so they cannot stack on
/// Polly's; the stream itself is owned by the turn-lifetime token the caller passes in
/// (turn-budgets.md section 4a) - HTTP-layer timeouts never cap a healthy generation.
/// </para>
///
/// <para>
/// <b>Integration-only:</b> the live wire path (a real vLLM server) cannot run in CI;
/// it is exercised by the Python mock upstreams + staging. The unit tests drive this
/// client through a stubbed <c>HttpMessageHandler</c> serving a canned SSE stream, and
/// test <see cref="OpenAIChatRequestBuilder"/> / <see cref="OpenAIStreamParser"/> directly.
/// </para>
/// </summary>
public sealed class OpenAIChatModelClient : IChatModelClient
{
    /// <summary>The shared named <c>HttpClient</c> for OpenAI-compatible upstreams.</summary>
    public const string HttpClientName = "openai";

    private readonly OpenAIClient _client;
    private readonly ChatProviderParameters _parameters;
    private readonly ILogger<OpenAIChatModelClient> _logger;

    /// <summary>
    /// Construct for one configured provider: its connection (<see cref="ChatProviderParameters.BaseUrl"/>
    /// + <see cref="ChatProviderParameters.ApiKey"/>) shapes the SDK client; its model + sampling shape
    /// each request. The <paramref name="http"/> is the shared chat client (Polly pre-stream pipeline +
    /// wire trace + infinite timeout) - the SDK sets the per-provider endpoint + bearer, so one
    /// transport serves every provider.
    /// </summary>
    public OpenAIChatModelClient(
        HttpClient http,
        ChatProviderParameters parameters,
        ILogger<OpenAIChatModelClient> logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        _parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _client = CreateSdkClient(http, _parameters.BaseUrl, _parameters.ApiKey);
    }

    /// <summary>
    /// Build the SDK client over the shared <c>HttpClient</c>: the transport wraps the typed client so
    /// the named-client handler chain (Polly pre-stream pipeline, wire trace, client timeout policy)
    /// keeps owning the HTTP layer; the per-caller endpoint + secret bearer (F8) ride the SDK
    /// credential/options. The SDK's own retry/timeout knobs are disabled - exactly one resilience owner.
    /// Shared by the chat client (per provider) and the embeddings client (the Gert:OpenAI connection).
    /// </summary>
    internal static OpenAIClient CreateSdkClient(HttpClient http, string baseUrl, string? apiKey) =>
        new(
            // vLLM without an api key ignores Authorization; the SDK requires a
            // non-empty credential, hence the placeholder.
            new ApiKeyCredential(string.IsNullOrEmpty(apiKey) ? "unused" : apiKey),
            new OpenAIClientOptions
            {
                Endpoint = V1Endpoint(baseUrl),
                Transport = new HttpClientPipelineTransport(http),
                NetworkTimeout = Timeout.InfiniteTimeSpan,
                RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
            });

    /// <summary>
    /// The SDK expects the versioned API root (it appends <c>chat/completions</c> etc.),
    /// while <see cref="ChatProviderParameters.BaseUrl"/> is the bare server base - accept either
    /// (the "/v1 gotcha", installation/configuration.md section 3).
    /// </summary>
    internal static Uri V1Endpoint(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        if (!trimmed.EndsWith("/v1", StringComparison.Ordinal))
        {
            trimmed += "/v1";
        }

        return new Uri(trimmed, UriKind.Absolute);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatModelChunk> StreamAsync(
        ChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The provider fixes the upstream model + sampling; request.ModelId is the
        // provider slug (already used to pick THIS client), not the upstream model.
        var (messages, chatOptions) = OpenAIChatRequestBuilder.Build(request, _parameters);
        var parser = new OpenAIStreamParser();

        var updates = _client
            .GetChatClient(_parameters.Model)
            .CompleteChatStreamingAsync(messages, chatOptions, cancellationToken);

        await using var enumerator = updates.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            bool moved;
            try
            {
                moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (ClientResultException ex)
            {
                // The error body carries the actual diagnostic (template/validation
                // message) - a bare status code is undebuggable from the event log.
                // Re-thrown as HttpRequestException to keep the port's error contract.
                var detail = ErrorDetail(ex);
                _logger.LogWarning(
                    "Chat completion failed with status {Status}: {Detail}.",
                    ex.Status, detail);
                throw new HttpRequestException(
                    $"Chat completion failed with status {ex.Status}: {detail}",
                    ex,
                    ex.Status > 0 ? (HttpStatusCode)ex.Status : null);
            }

            if (!moved)
            {
                break;
            }

            foreach (var chunk in parser.Parse(enumerator.Current))
            {
                yield return chunk;
            }
        }

        // Surface a terminal finish chunk if the server closed without one inline.
        foreach (var chunk in parser.Flush())
        {
            yield return chunk;
        }

        // Leak accounting (chat-and-tools.md section tool-call robustness): salvaged
        // calls mean the server-side tool parser is missing calls the model DID
        // make - worth an operator's attention; dropped markup means the model
        // emitted tool syntax too mangled to recover.
        if (parser.SalvagedToolCalls > 0)
        {
            _logger.LogWarning(
                "Salvaged {Count} tool call(s) from <tool_call> markup that leaked into content - check the vLLM --tool-call-parser setting.",
                parser.SalvagedToolCalls);
        }

        if (parser.DroppedLeakChars > 0)
        {
            _logger.LogWarning(
                "Dropped {Chars} characters of unparseable leaked tool-call markup from the reply.",
                parser.DroppedLeakChars);
        }
    }

    /// <summary>
    /// Best-effort extraction of the error response body, bounded so a misbehaving
    /// server can't balloon the exception message.
    /// </summary>
    private static string ErrorDetail(ClientResultException ex)
    {
        string detail;
        try
        {
            detail = ex.GetRawResponse()?.Content.ToString()?.Trim() ?? string.Empty;
        }
        catch (InvalidOperationException)
        {
            detail = string.Empty;
        }

        if (detail.Length == 0)
        {
            detail = ex.Message;
        }

        return detail.Length > 512 ? detail[..512] + "..." : detail;
    }
}
