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

    public int? MaxTokens { get; init; }

    public IReadOnlyList<string>? Stop { get; init; }

    public int? Seed { get; init; }
}
