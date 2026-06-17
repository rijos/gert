namespace Gert.Chat.OpenAI;

/// <summary>
/// The embeddings connection + its HTTP resilience (the <c>Parameters</c> bag of
/// <see cref="EmbeddingsOptions"/>, bound from <c>Gert:Embeddings:Parameters</c>). This is
/// what changes when the embeddings <see cref="EmbeddingsOptions.Type"/> changes - the base
/// URL, the secret bearer, the model + expected dimension, and the per-item timeout/retry the
/// embeddings <c>HttpClient</c> uses. The <see cref="ApiKey"/> is a <b>secret</b> that comes
/// from env / <c>dotnet user-secrets</c> / a secret store, never <c>appsettings.json</c>
/// (security F8); <c>appsettings.json</c> carries only the non-secret defaults.
/// </summary>
public sealed class EmbeddingsParameters
{
    /// <summary>Base URL of the OpenAI-compatible embeddings server, e.g. <c>http://vllm:8000</c>.</summary>
    public string BaseUrl { get; set; } = "http://localhost:8000";

    /// <summary>Bearer key for the embeddings upstream. <b>Secret</b> (F8) - env/user-secrets only.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Embedding model id sent as <c>model</c> on <c>/v1/embeddings</c>.</summary>
    public string Model { get; set; } = "bge-m3";

    /// <summary>Expected embedding dimension (bge-m3 = 1024). Mismatch is rejected.</summary>
    public int Dimensions { get; set; } = 1024;

    /// <summary>
    /// Max wait, per attempt, for the upstream to <b>accept</b> a request - time to
    /// response headers, in seconds - <b>not</b> the stream duration. The buffered embeddings
    /// body is covered by that client's finite overall timeout (which sits just outside the
    /// resilience pipeline's total). Per item: this is the embeddings path's own resilience,
    /// independent of any chat provider's. Default 120.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Retry attempts on transient pre-stream failure (connect / headers phase). Embedding
    /// POSTs are idempotent (a pure function of the input batch), so options-bound retries
    /// are safe. <c>0</c> disables retries. Default 2.
    /// </summary>
    public int RetryCount { get; set; } = 2;
}
