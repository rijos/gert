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

    /// <summary>Enabled tool ids for this turn. Empty on the no-tool path (tool loop lands later).</summary>
    public IReadOnlyList<string> ToolIds { get; init; } = [];

    /// <summary>Generation params resolved from the conversation, forwarded to the model.</summary>
    public double? Temperature { get; init; }

    public double? TopP { get; init; }

    public int? MaxTokens { get; init; }

    public IReadOnlyList<string>? Stop { get; init; }

    public int? Seed { get; init; }
}
