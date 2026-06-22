using System.ClientModel;
using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Embeddings;
using EmbeddingGenerationOptions = Microsoft.Extensions.AI.EmbeddingGenerationOptions;

namespace Gert.Chat.OpenAI;

/// <summary>
/// Gert's embedding port: a Microsoft.Extensions.AI
/// <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> over an OpenAI-compatible
/// <c>/v1/embeddings</c> endpoint (vLLM serving bge-m3 in the reference deployment). Implemented
/// DIRECTLY over the OpenAI SDK's <c>EmbeddingClient</c> rather than via
/// <c>.AsIEmbeddingGenerator()</c> so it can preserve Gert's fail-closed reassembly: order-by-index
/// (the OpenAI/vLLM response may, in principle, arrive out of order - index decides; M.E.AI's
/// <c>GeneratedEmbeddings</c> drops the source index, so the stock adapter cannot reorder) plus the
/// configured-dimension (bge-m3 = 1024) and one-vector-per-input count assertions, because a mismatch
/// is an upstream contract violation that must not silently corrupt the vec0 index (decisions #1, #13).
///
/// <para>
/// The typed <c>HttpClient</c> (base URL + secret bearer key, F8) and the Polly timeout+retry
/// pipeline are configured by <see cref="ServiceCollectionExtensions"/> with the SDK's own
/// retry/timeout disabled. Unlike streaming chat, this call is <b>buffered</b>, so its named client
/// keeps a finite overall timeout outside the pipeline total; embedding POSTs are idempotent (a pure
/// function of the input batch), so options-bound retries are safe.
/// </para>
/// </summary>
public sealed class OpenAIEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    /// <summary>
    /// The named <c>HttpClient</c> for the embeddings path - split from the chat client because
    /// their timeout ownership differs: chat streams (infinite client timeout), embeddings buffer
    /// (finite). The readiness probe anchors on this name (chat clients carry no base address).
    /// </summary>
    public const string HttpClientName = "openai-embeddings";

    private readonly OpenAIClient _client;
    private readonly EmbeddingsParameters _parameters;
    private readonly ILogger<OpenAIEmbeddingGenerator> _logger;

    /// <summary>Construct over the configured typed client + the bound embeddings parameters.</summary>
    public OpenAIEmbeddingGenerator(
        HttpClient http,
        EmbeddingsParameters parameters,
        ILogger<OpenAIEmbeddingGenerator> logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        _parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _client = OpenAISdkClient.CreateSdkClient(http, _parameters.BaseUrl, _parameters.ApiKey);
    }

    /// <inheritdoc />
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        var inputs = values as IReadOnlyList<string> ?? values.ToList();
        if (inputs.Count == 0)
        {
            return [];
        }

        OpenAIEmbeddingCollection embeddings;
        try
        {
            var result = await _client
                .GetEmbeddingClient(_parameters.Model)
                .GenerateEmbeddingsAsync(inputs, options: null, cancellationToken)
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

        var generated = new GeneratedEmbeddings<Embedding<float>>(inputs.Count);
        foreach (var vector in MapResponse(embeddings, inputs.Count, _parameters.Dimensions))
        {
            generated.Add(new Embedding<float>(vector));
        }

        return generated;
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc />
    public void Dispose()
    {
    }

    /// <summary>
    /// Pure response mapper - testable without an <c>HttpClient</c>. Orders the vectors by
    /// <c>index</c> and asserts the vector count + dimension. Either mismatch is an upstream contract
    /// violation and throws.
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
