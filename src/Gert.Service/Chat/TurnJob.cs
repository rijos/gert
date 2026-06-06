using Gert.Model.Chat;
using Gert.Service.External;

namespace Gert.Service.Chat;

/// <summary>
/// The off-thread work item for one assistant turn (chat-and-tools.md § detached
/// turns) — everything <see cref="TurnRunner"/> needs, captured at plan time, so
/// the worker never touches the request scope (mirrors <c>IngestJob</c>):
/// <list type="bullet">
///   <item><b>Identity</b> (<see cref="Iss"/>/<see cref="Sub"/>): the database
///   path scope. The runner opens repos with these, never with a request
///   <see cref="IUserContext"/> (unavailable off-thread).</item>
///   <item><b>Entitlement snapshot</b> (<see cref="AllowedToolIds"/>): the
///   execution-time re-check ceiling. Snapshotted from the validated JWT at plan
///   time — the claim is the ceiling even off-thread (auth.md).</item>
///   <item><b>The prepared turn</b>: history, offered tools, generation params —
///   the old <c>ChatTurn</c>, absorbed here.</item>
/// </list>
/// </summary>
public sealed record TurnJob
{
    // --- identity (the folder/scope anchor) ---------------------------------
    public required string Iss { get; init; }

    public required string Sub { get; init; }

    public required string Username { get; init; }

    public bool IsAdmin { get; init; }

    /// <summary>Entitlement snapshot — the hard ceiling for tool execution.</summary>
    public required IReadOnlySet<string> AllowedToolIds { get; init; }

    // --- the turn ------------------------------------------------------------
    public required string Pid { get; init; }

    public required string ConversationId { get; init; }

    /// <summary>The user message persisted at plan time.</summary>
    public required string UserMessageId { get; init; }

    /// <summary>The assistant row inserted (status=streaming) at plan time.</summary>
    public required string AssistantMessageId { get; init; }

    /// <summary>
    /// The assistant row's seq — returned by POST as the subscribe cursor (all
    /// of the turn's events get a later seq).
    /// </summary>
    public required long AssistantSeq { get; init; }

    public required string ModelId { get; init; }

    /// <summary>Prior turns (system prompt excluded), ending with the user message.</summary>
    public required IReadOnlyList<ChatModelMessage> History { get; init; }

    /// <summary>Offered tool capability ids (requested ∩ enabled ∩ entitled ∩ registry).</summary>
    public IReadOnlyList<string> ToolIds { get; init; } = [];

    /// <summary>Model-facing specs for the offered tools, same order as <see cref="ToolIds"/>.</summary>
    public IReadOnlyList<ChatToolSpec> Tools { get; init; } = [];

    /// <summary>The project's pinned instructions (step 0), or null.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Effective reasoning toggle for the turn (request ?? conversation; null = model default).</summary>
    public bool? Thinking { get; init; }

    /// <summary>Effective interleaved-thinking toggle (request ?? conversation; null = model default).</summary>
    public bool? PreserveThinking { get; init; }

    // --- generation params (conversation overrides ?? per-model user settings) ---
    public double? Temperature { get; init; }

    public double? TopP { get; init; }

    public int? MaxTokens { get; init; }

    public IReadOnlyList<string>? Stop { get; init; }

    public int? Seed { get; init; }
}
