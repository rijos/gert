using System.ClientModel;
using System.ClientModel.Primitives;
using OpenAI;

namespace Gert.Chat.OpenAI;

/// <summary>
/// Construction helpers for the official OpenAI SDK client over Gert's named, Polly-wrapped
/// <c>HttpClient</c> transport (tech-stack.md section Model API). Shared by the chat plugin
/// (per provider slug) and the embeddings generator (the <c>Gert:Embeddings</c> connection):
/// both ride the same transport policy so the named-client handler chain (Polly pre-stream
/// pipeline, wire trace, client timeout) stays the single resilience owner and the SDK's own
/// retry/timeout are disabled.
/// </summary>
internal static class OpenAISdkClient
{
    /// <summary>
    /// The named <c>HttpClient</c> for one chat provider's OpenAI-compatible upstream. One client
    /// per provider slug (each carries that provider's own pre-stream resilience pipeline + wire
    /// trace + infinite timeout), so the transport for a hot conversation is the provider's, not a
    /// single shared one.
    /// </summary>
    public static string HttpClientNameFor(string providerId) => $"openai:{providerId}";

    /// <summary>
    /// Build the SDK client over the shared <c>HttpClient</c>: the transport wraps the typed client
    /// so the named-client handler chain (Polly pre-stream pipeline, wire trace, client timeout
    /// policy) keeps owning the HTTP layer; the per-caller endpoint + secret bearer (F8) ride the SDK
    /// credential/options. The SDK's own retry/timeout knobs are disabled - exactly one resilience
    /// owner. vLLM without an api key ignores Authorization; the SDK requires a non-empty credential,
    /// hence the placeholder.
    /// </summary>
    public static OpenAIClient CreateSdkClient(HttpClient http, string baseUrl, string? apiKey) =>
        new(
            new ApiKeyCredential(string.IsNullOrEmpty(apiKey) ? "unused" : apiKey),
            new OpenAIClientOptions
            {
                Endpoint = V1Endpoint(baseUrl),
                Transport = new HttpClientPipelineTransport(http),
                NetworkTimeout = Timeout.InfiniteTimeSpan,
                RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
            });

    /// <summary>
    /// The SDK expects the versioned API root (it appends <c>chat/completions</c> etc.), while the
    /// configured base URL is the bare server base - accept either (the "/v1 gotcha",
    /// installation/configuration.md section 3).
    /// </summary>
    public static Uri V1Endpoint(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        if (!trimmed.EndsWith("/v1", StringComparison.Ordinal))
        {
            trimmed += "/v1";
        }

        return new Uri(trimmed, UriKind.Absolute);
    }
}
