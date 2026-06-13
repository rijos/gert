namespace Gert.External.OpenAI;

/// <summary>
/// The embeddings connection + the shared chat-transport resilience defaults
/// (<c>Gert:OpenAI</c>). Chat <em>connection + sampling</em> now live per provider
/// in <c>Gert:Providers</c> (configuration.md section providers); this section keeps the
/// embeddings upstream and the timeout/retry knobs the shared chat <c>HttpClient</c>
/// reuses. The <see cref="ApiKey"/> is a <b>secret</b> that comes from env /
/// <c>dotnet user-secrets</c> / a secret store, never <c>appsettings.json</c>
/// (security F8). <c>appsettings.json</c> carries only the non-secret defaults.
/// </summary>
public sealed class OpenAIOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Gert:OpenAI";

    /// <summary>Base URL of the OpenAI-compatible embeddings server, e.g. <c>http://vllm:8000</c>.</summary>
    public string BaseUrl { get; set; } = "http://localhost:8000";

    /// <summary>Bearer key for the embeddings upstream. <b>Secret</b> (F8) - env/user-secrets only.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Embedding model id sent as <c>model</c> on <c>/v1/embeddings</c>.</summary>
    public string EmbeddingModelId { get; set; } = "bge-m3";

    /// <summary>Expected embedding dimension (bge-m3 = 1024). Mismatch is rejected.</summary>
    public int EmbeddingDimensions { get; set; } = 1024;

    /// <summary>
    /// Max wait, per attempt, for the upstream to <b>accept</b> a request - time to
    /// response headers, in seconds - <b>not</b> the stream duration. The chat SSE body
    /// that follows the headers is bounded by the turn-lifetime token, never by HTTP
    /// timeouts (turn-budgets.md section 4a, <c>MaxTurnDuration</c>); the buffered embeddings
    /// body is covered by that client's finite overall timeout. Default 120.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Retry attempts on transient pre-stream failure (connect / headers phase), for both
    /// chat and embeddings. Safe for the non-idempotent chat POST because the pipeline
    /// completes at the response headers - a retried attempt means no tokens were ever
    /// streamed; embedding POSTs are idempotent outright. <c>0</c> disables retries.
    /// Default 2.
    /// </summary>
    public int RetryCount { get; set; } = 2;
}
