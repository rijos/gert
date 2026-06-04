using Gert.Service.External;

namespace Gert.Service.Chat;

/// <summary>
/// The in-memory context for a single chat turn, produced by
/// <see cref="IChatService.StartTurnAsync"/> (phase 1) and consumed by
/// <see cref="IChatService.RunAsync"/> (phase 2). It is a pure value carried
/// across the two calls <b>within one HTTP request</b> — never persisted, never
/// cached, and never keyed by a turn id. GERT runs as multiple stateless
/// instances (decisions §4 / review #10), so no server-side turn registry exists:
/// everything <see cref="RunAsync"/> needs is captured here in phase 1, and each
/// phase opens <c>chat.db</c> per-use rather than holding a DB handle across the
/// two calls.
/// </summary>
public sealed record ChatTurn
{
    /// <summary>Project id — a UUID or the literal <c>default</c> — used to re-open <c>chat.db</c> in phase 2.</summary>
    public required string Pid { get; init; }

    /// <summary>Target conversation id.</summary>
    public required string ConversationId { get; init; }

    /// <summary>
    /// The assistant message id, generated in phase 1 so the <c>message_start</c>
    /// event and the persisted assistant row share one id.
    /// </summary>
    public required string AssistantMessageId { get; init; }

    /// <summary>Resolved model id (request → conversation → default), fixed at phase 1.</summary>
    public required string ModelId { get; init; }

    /// <summary>The prior turns (incl. the just-persisted user message) to send upstream.</summary>
    public required IReadOnlyList<ChatModelMessage> Messages { get; init; }

    /// <summary>
    /// The offered tool ids for this turn — the intersection
    /// <c>requested ∩ conversation-enabled ∩ entitlement ∩ registry</c>, resolved
    /// in phase 1 (auth.md § the claim is the ceiling). Empty on the no-tool path.
    /// The matching specs (advertised to the model) are in <see cref="Tools"/>.
    /// </summary>
    public IReadOnlyList<string> ToolIds { get; init; } = [];

    /// <summary>
    /// The model-facing specs for the offered tools, in the same order as
    /// <see cref="ToolIds"/> — exactly what <see cref="IChatService.RunAsync"/>
    /// advertises to the model. A tool the user isn't entitled to never appears
    /// here, even if requested.
    /// </summary>
    public IReadOnlyList<External.ChatToolSpec> Tools { get; init; } = [];

    /// <summary>
    /// The project's pinned system context (step 0) — the always-injected
    /// instructions prepended as a <c>system</c> message before the prior turns,
    /// or <c>null</c> when the project has none.
    /// </summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Generation params resolved from the conversation, forwarded to the model.</summary>
    public double? Temperature { get; init; }

    public double? TopP { get; init; }

    public int? MaxTokens { get; init; }

    public IReadOnlyList<string>? Stop { get; init; }

    public int? Seed { get; init; }
}
