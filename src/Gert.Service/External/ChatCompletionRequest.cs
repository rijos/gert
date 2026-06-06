namespace Gert.Service.External;

/// <summary>A request to the chat model — messages, model id, advertised tools, params.</summary>
public sealed record ChatCompletionRequest
{
    public required string ModelId { get; init; }

    public required IReadOnlyList<ChatModelMessage> Messages { get; init; }

    /// <summary>Tools offered this turn (already intersected to the entitlement ceiling).</summary>
    public IReadOnlyList<ChatToolSpec> Tools { get; init; } = [];

    public double? Temperature { get; init; }

    public double? TopP { get; init; }

    /// <summary>OpenAI-style presence penalty (-2..2); null omits the field.</summary>
    public double? PresencePenalty { get; init; }

    public int? MaxTokens { get; init; }

    public IReadOnlyList<string>? Stop { get; init; }

    public int? Seed { get; init; }

    /// <summary>
    /// Reasoning toggle (<c>chat_template_kwargs.enable_thinking</c>); null
    /// omits the kwarg and leaves the server/template default.
    /// </summary>
    public bool? EnableThinking { get; init; }

    /// <summary>
    /// Interleaved thinking (<c>chat_template_kwargs.preserve_thinking</c>,
    /// Qwen3.6); null omits the kwarg. Pair with assistant
    /// <see cref="ChatModelMessage.ReasoningContent"/> history.
    /// </summary>
    public bool? PreserveThinking { get; init; }
}
