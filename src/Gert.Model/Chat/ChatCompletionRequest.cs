namespace Gert.Model.Chat;

/// <summary>
/// A request to the chat model - messages, the selected provider id, advertised
/// tools, and the per-round token cap. Sampling (temperature/top_p/penalties/the
/// vLLM extensions) and the thinking template kwargs are <b>not</b> here: they ride
/// the selected provider's configuration, applied by the adapter
/// (<c>OpenAIChatRequestBuilder</c>). <see cref="MaxTokens"/> is the one sampling
/// field the request carries because it is the turn-budget cap the runner computes
/// (<c>TurnOptions.MaxTokensPerRound</c>), not a provider preset.
/// </summary>
public sealed record ChatCompletionRequest
{
    /// <summary>The selected provider id (the <c>Gert:Chat:Providers</c> slug); picks the client.</summary>
    public required string ModelId { get; init; }

    public required IReadOnlyList<ChatModelMessage> Messages { get; init; }

    /// <summary>Tools offered this turn (already intersected to the entitlement ceiling).</summary>
    public IReadOnlyList<ChatToolSpec> Tools { get; init; } = [];

    /// <summary>Per-round completion cap (<c>max_completion_tokens</c>); null leaves it to the provider.</summary>
    public int? MaxTokens { get; init; }
}
