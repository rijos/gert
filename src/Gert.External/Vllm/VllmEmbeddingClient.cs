using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gert.Service.External;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gert.External.Vllm;

/// <summary>
/// Real <see cref="IEmbeddingClient"/> over a vLLM OpenAI-compatible
/// <c>/v1/embeddings</c> endpoint. Sends the batch of input texts and reads back one
/// vector per input, in order, asserting the configured dimension (bge-m3 = 1024).
///
/// <para>
/// The typed <c>HttpClient</c> (base URL + secret bearer key, F8) and the Polly
/// timeout+retry pipeline are configured by <see cref="ServiceCollectionExtensions"/>.
/// Unlike chat, this call is <b>buffered</b>, so its named client keeps a finite overall
/// timeout sitting just outside the pipeline's total; embedding POSTs are idempotent
/// (a pure function of the input batch), so options-bound retries are safe.
/// </para>
///
/// <para>
/// <b>Integration-only:</b> the live wire path needs a real vLLM server (U13 mock +
/// staging). Unit tests drive it through a stubbed <c>HttpMessageHandler</c> with a
/// canned 1024-dim response and assert the request/response shape.
/// </para>
/// </summary>
public sealed class VllmEmbeddingClient : IEmbeddingClient
{
    /// <summary>
    /// The named <c>HttpClient</c> for the vLLM embeddings path — split from the chat
    /// client because their timeout ownership differs: chat streams (infinite client
    /// timeout), embeddings buffer (finite).
    /// </summary>
    public const string HttpClientName = "vllm-embeddings";

    private readonly HttpClient _http;
    private readonly VllmOptions _options;
    private readonly ILogger<VllmEmbeddingClient> _logger;

    /// <summary>Construct over the configured typed client + options.</summary>
    public VllmEmbeddingClient(
        HttpClient http,
        IOptions<VllmOptions> options,
        ILogger<VllmEmbeddingClient> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);

        if (texts.Count == 0)
        {
            return [];
        }

        var body = BuildRequest(texts, _options.EmbeddingModelId);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/embeddings")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        using var response = await _http
            .SendAsync(httpRequest, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("vLLM embeddings failed with status {Status}.", (int)response.StatusCode);
            response.EnsureSuccessStatusCode();
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseResponse(json, texts.Count, _options.EmbeddingDimensions);
    }

    /// <summary>Pure request builder — testable without an <c>HttpClient</c>.</summary>
    public static JsonObject BuildRequest(IReadOnlyList<string> texts, string modelId)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentException.ThrowIfNullOrEmpty(modelId);

        var input = new JsonArray();
        foreach (var t in texts)
        {
            input.Add(t);
        }

        return new JsonObject
        {
            ["model"] = modelId,
            ["input"] = input,
            ["encoding_format"] = "float",
        };
    }

    /// <summary>
    /// Pure response parser — testable without an <c>HttpClient</c>. Reads
    /// <c>data[].embedding</c>, orders by <c>data[].index</c>, and asserts the
    /// vector count + dimension. A mismatch is an upstream contract violation and throws.
    /// </summary>
    public static IReadOnlyList<float[]> ParseResponse(string json, int expectedCount, int expectedDimensions)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("vLLM embeddings response missing 'data' array.");
        }

        var byIndex = new SortedDictionary<int, float[]>();
        var fallbackIndex = 0;
        foreach (var item in data.EnumerateArray())
        {
            var index = item.TryGetProperty("index", out var idx) && idx.ValueKind == JsonValueKind.Number
                ? idx.GetInt32()
                : fallbackIndex;
            fallbackIndex++;

            if (!item.TryGetProperty("embedding", out var emb) || emb.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("vLLM embeddings response item missing 'embedding'.");
            }

            var vector = new float[emb.GetArrayLength()];
            var i = 0;
            foreach (var v in emb.EnumerateArray())
            {
                vector[i++] = v.GetSingle();
            }

            if (vector.Length != expectedDimensions)
            {
                throw new InvalidOperationException(
                    $"vLLM embedding dimension {vector.Length} != expected {expectedDimensions}.");
            }

            byIndex[index] = vector;
        }

        if (byIndex.Count != expectedCount)
        {
            throw new InvalidOperationException(
                $"vLLM returned {byIndex.Count} embeddings for {expectedCount} inputs.");
        }

        return byIndex.Values.ToList();
    }
}
