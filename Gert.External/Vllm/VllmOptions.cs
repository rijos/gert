namespace Gert.External.Vllm;

/// <summary>
/// Connection + model options for the vLLM OpenAI-compatible upstream (chat +
/// embeddings). Bound from configuration section <c>Gert:Vllm</c>; the
/// <see cref="ApiKey"/> is a <b>secret</b> that comes from env /
/// <c>dotnet user-secrets</c> / a secret store, never <c>appsettings.json</c>
/// (security F8). <c>appsettings.json</c> carries only the non-secret defaults
/// (base URL, model ids).
/// </summary>
public sealed class VllmOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Gert:Vllm";

    /// <summary>Base URL of the OpenAI-compatible server, e.g. <c>http://vllm:8000</c>.</summary>
    public string BaseUrl { get; set; } = "http://localhost:8000";

    /// <summary>Bearer key for the upstream. <b>Secret</b> (F8) — env/user-secrets only.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Chat model id sent as <c>model</c> on <c>/v1/chat/completions</c>.</summary>
    public string ChatModelId { get; set; } = "default";

    /// <summary>Embedding model id sent as <c>model</c> on <c>/v1/embeddings</c>.</summary>
    public string EmbeddingModelId { get; set; } = "bge-m3";

    /// <summary>Expected embedding dimension (bge-m3 = 1024). Mismatch is rejected.</summary>
    public int EmbeddingDimensions { get; set; } = 1024;

    /// <summary>Per-request timeout for a single attempt (seconds). Polly wraps this.</summary>
    public int RequestTimeoutSeconds { get; set; } = 120;

    /// <summary>Retry attempts on transient upstream failure.</summary>
    public int RetryCount { get; set; } = 2;
}
