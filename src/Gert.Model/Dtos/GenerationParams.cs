namespace Gert.Model.Dtos;

/// <summary>
/// Generation overrides configurable at user / project / conversation level —
/// the <c>params_json</c> shape (configuration.md § 4; storage-and-data.md
/// § chat.db). All optional; unset fields inherit the level above, and the
/// server clamps each to admin-set bounds.
/// </summary>
public sealed record GenerationParams
{
    public double? Temperature { get; init; }

    public double? TopP { get; init; }

    /// <summary>
    /// OpenAI-style presence penalty (-2..2). Qwen3.6's instruct (non-thinking)
    /// mode prescribes 1.5 to suppress repetition loops; thinking mode wants 0.
    /// </summary>
    public double? PresencePenalty { get; init; }

    public int? MaxTokens { get; init; }

    /// <summary>Stop sequences.</summary>
    public IReadOnlyList<string>? Stop { get; init; }

    public int? Seed { get; init; }
}
