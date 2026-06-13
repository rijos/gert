using System.ClientModel;
using System.Net;
using Gert.Service.External;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Embeddings;

namespace Gert.External.OpenAI;

/// <summary>
/// Real <see cref="IEmbeddingClient"/> over an OpenAI-compatible <c>/v1/embeddings</c>
/// endpoint - vLLM serving bge-m3 in the reference deployment. Built on the official
/// OpenAI SDK; sends the batch of input texts and reads back one vector per input,
/// ordered by <c>index</c>, asserting the configured dimension (bge-m3 = 1024).
///
/// <para>
/// The typed <c>HttpClient</c> (base URL + secret bearer key, F8) and the Polly
/// timeout+retry pipeline are configured by <see cref="ServiceCollectionExtensions"/>;
/// the SDK pipeline rides it via the same transport wrapping as the chat client, with
/// the SDK's own retry/timeout disabled. Unlike chat, this call is <b>buffered</b>, so
/// its named client keeps a finite overall timeout sitting just outside the pipeline's
/// total; embedding POSTs are idempotent (a pure function of the input batch), so
/// options-bound retries are safe.
/// </para>
///
/// <para>
/// <b>Integration-only:</b> the live wire path needs a real vLLM server (mock +
/// staging). Unit tests drive it through a stubbed <c>HttpMessageHandler</c> with a
/// canned 1024-dim response and assert the request/response shape.
/// </para>
/// </summary>
public sealed class OpenAIEmbeddingClient : IEmbeddingClient
{
    /// <summary>
    /// The named <c>HttpClient</c> for the embeddings path - split from the chat
    /// client because their timeout ownership differs: chat streams (infinite client
    /// timeout), embeddings buffer (finite).
    /// </summary>
    public const string HttpClientName = "openai-embeddings";

    private readonly OpenAIClient _client;
    private readonly OpenAIOptions _options;
    private readonly ILogger<OpenAIEmbeddingClient> _logger;

    /// <summary>Construct over the configured typed client + options.</summary>
    public OpenAIEmbeddingClient(
        HttpClient http,
        IOptions<OpenAIOptions> options,
        ILogger<OpenAIEmbeddingClient> logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _client = OpenAIChatModelClient.CreateSdkClient(http, _options.BaseUrl, _options.ApiKey);
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

        OpenAIEmbeddingCollection embeddings;
        try
        {
            var result = await _client
                .GetEmbeddingClient(_options.EmbeddingModelId)
                .GenerateEmbeddingsAsync(texts, options: null, cancellationToken)
                .ConfigureAwait(false);
            embeddings = result.Value;
        }
        catch (ClientResultException ex)
        {
            // Keep the port's error contract (HttpRequestException on upstream failure).
            _logger.LogWarning("Embeddings request failed with status {Status}.", ex.Status);
            throw new HttpRequestException(
                $"Embeddings request failed with status {ex.Status}.",
                ex,
                ex.Status > 0 ? (HttpStatusCode)ex.Status : null);
        }

        return MapResponse(embeddings, texts.Count, _options.EmbeddingDimensions);
    }

    /// <summary>
    /// Pure response mapper - testable without an <c>HttpClient</c>. Orders the
    /// vectors by <c>index</c> and asserts the vector count + dimension. A mismatch
    /// is an upstream contract violation and throws.
    /// </summary>
    public static IReadOnlyList<float[]> MapResponse(
        OpenAIEmbeddingCollection embeddings, int expectedCount, int expectedDimensions)
    {
        ArgumentNullException.ThrowIfNull(embeddings);

        var byIndex = new SortedDictionary<int, float[]>();
        foreach (var embedding in embeddings)
        {
            var vector = embedding.ToFloats().ToArray();
            if (vector.Length != expectedDimensions)
            {
                throw new InvalidOperationException(
                    $"Embedding dimension {vector.Length} != expected {expectedDimensions}.");
            }

            byIndex[embedding.Index] = vector;
        }

        if (byIndex.Count != expectedCount)
        {
            throw new InvalidOperationException(
                $"Upstream returned {byIndex.Count} embeddings for {expectedCount} inputs.");
        }

        return byIndex.Values.ToList();
    }
}
