namespace Gert.Chat.OpenAI;

/// <summary>
/// The <c>OpenAI</c> provider type's <c>Parameters</c> bag (configuration.md
/// section providers) - connection + the full sampling set for one named preset.
/// Bound (as named options keyed by the provider slug) from
/// <c>Gert:Chat:Providers:&lt;slug&gt;:Parameters</c> by the OpenAI plugin. OpenAI REST-spec
/// sampling is typed (null = omit, so the upstream falls back to its own
/// default); everything outside the spec rides <see cref="Extra"/>.
/// </summary>
public sealed class ChatProviderParameters
{
    /// <summary>Base URL of the OpenAI-compatible server, e.g. <c>http://vllm:8000</c>.</summary>
    public string BaseUrl { get; set; } = "http://localhost:8000";

    /// <summary>The upstream model id sent as <c>model</c> on <c>/v1/chat/completions</c>.</summary>
    public string Model { get; set; } = "default";

    /// <summary>
    /// Bearer key for this provider. <b>Secret</b> (F8) - env / <c>dotnet
    /// user-secrets</c> only, never <c>appsettings.json</c>. Empty for a keyless vLLM.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Max wait, per attempt, for this provider's upstream to <b>accept</b> a request - time
    /// to response headers, in seconds - <b>not</b> the stream duration. The chat SSE body that
    /// follows the headers is bounded by the turn-lifetime token, never by HTTP timeouts
    /// (turn-budgets.md section 4a, <c>MaxTurnDuration</c>). Per item: each provider carries its
    /// own resilience, so the chat transport is one named client per provider slug. Default 120.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Retry attempts on transient pre-stream failure (connect / headers phase). Safe for the
    /// non-idempotent chat POST because the pipeline completes at the response headers - a
    /// retried attempt means no tokens were ever streamed. <c>0</c> disables retries. Default 2.
    /// </summary>
    public int RetryCount { get; set; } = 2;

    // --- OpenAI REST-spec sampling (typed; null = omit) ----------------------
    public double? Temperature { get; set; }

    public double? TopP { get; set; }

    public double? PresencePenalty { get; set; }

    public double? FrequencyPenalty { get; set; }

    public int? Seed { get; set; }

    /// <summary>Stop sequences.</summary>
    public IReadOnlyList<string>? Stop { get; set; }

    /// <summary>
    /// Everything outside the OpenAI REST spec, keyed by JSON path under the
    /// request root (dotted; <c>$.</c> is prepended at apply time) with a string
    /// value parsed to its JSON type. This is where the vLLM extensions
    /// (<c>top_k</c>, <c>min_p</c>, <c>repetition_penalty</c>) and the template
    /// kwargs (<c>chat_template_kwargs.enable_thinking</c> /
    /// <c>chat_template_kwargs.preserve_thinking</c>) live - config, not POCO fields.
    /// </summary>
    public Dictionary<string, string> Extra { get; set; } = new();

    /// <summary>
    /// Whether interleaved thinking is on for this provider - prior assistant
    /// <c>reasoning_content</c> is sent back upstream. Read from
    /// <c>Extra["chat_template_kwargs.preserve_thinking"]</c> (the one off-spec
    /// flag the request builder needs to gate history shaping).
    /// </summary>
    public bool PreserveThinking =>
        Extra.TryGetValue("chat_template_kwargs.preserve_thinking", out var v)
        && bool.TryParse(v, out var on) && on;
}
