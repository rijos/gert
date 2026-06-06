namespace Gert.Model.Chat;

/// <summary>
/// A conversation thread — mirrors the <c>conversations</c> row in a project's
/// <c>chat.db</c> (storage-and-data.md § chat.db). Tools and generation params
/// are persisted as JSON columns (<c>tools_json</c> / <c>params_json</c>); the
/// strongly-typed shapes are <see cref="Dtos.ToolToggles"/> and
/// <see cref="Dtos.GenerationParams"/>.
/// </summary>
public sealed record Conversation
{
    /// <summary>UUID primary key.</summary>
    public required string Id { get; init; }

    public required string Title { get; init; }

    /// <summary>Active model id, e.g. <c>qwen3-27b-fp8-mtp</c>.</summary>
    public required string ModelId { get; init; }

    /// <summary>Per-conversation tool toggles (the <c>tools_json</c> column).</summary>
    public Dtos.ToolToggles Tools { get; init; } = new();

    /// <summary>Per-conversation generation overrides (the <c>params_json</c> column).</summary>
    public Dtos.GenerationParams Params { get; init; } = new();

    /// <summary>
    /// Reasoning ("thinking") preference for this conversation: null = model
    /// default, otherwise an explicit on/off sent upstream as
    /// <c>chat_template_kwargs.enable_thinking</c>.
    /// </summary>
    public bool? Thinking { get; init; }

    /// <summary>
    /// Interleaved-thinking preference: when on, prior turns' reasoning is sent
    /// back upstream (assistant <c>reasoning_content</c> +
    /// <c>chat_template_kwargs.preserve_thinking</c>); null = model default
    /// (prior thinking stripped by the template).
    /// </summary>
    public bool? PreserveThinking { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public bool Archived { get; init; }
}
