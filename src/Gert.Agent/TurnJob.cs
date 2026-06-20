using Gert.Model;
using Gert.Model.Chat;
using Gert.Service;
using Gert.Service.Chat;
using Gert.Tools;

namespace Gert.Agent;

/// <summary>
/// The off-thread work item for one assistant turn (chat-and-tools.md section detached
/// turns) - everything <see cref="TurnRunner"/> needs, captured at plan time, so the
/// worker never touches the request scope (mirrors <c>IngestJob</c>):
/// <list type="bullet">
///   <item><b>Identity</b> (<see cref="Iss"/>/<see cref="Sub"/>): the database path
///   scope. The runner opens repos with these, never with a request
///   <see cref="IUserContext"/> (unavailable off-thread).</item>
///   <item><b>Entitlement snapshot</b> (<see cref="AllowedToolIds"/>): the
///   execution-time re-check ceiling, snapshotted from the validated JWT at plan time
///   so the claim stays the ceiling even off-thread (auth.md).</item>
///   <item><b>The prepared turn</b>: history, offered tools. Sampling rides the
///   selected provider, not the job.</item>
/// </list>
/// </summary>
public sealed record TurnJob
{
    public required string Iss { get; init; }

    public required string Sub { get; init; }

    public required string Username { get; init; }

    public bool IsAdmin { get; init; }

    /// <summary>Entitlement snapshot - the hard ceiling for tool execution.</summary>
    public required IReadOnlySet<string> AllowedToolIds { get; init; }

    public required string Pid { get; init; }

    public required string ConversationId { get; init; }

    /// <summary>The user message persisted at plan time.</summary>
    public required string UserMessageId { get; init; }

    /// <summary>The assistant row inserted (status=streaming) at plan time.</summary>
    public required string AssistantMessageId { get; init; }

    /// <summary>
    /// The assistant row's seq - returned by POST as the subscribe cursor (all
    /// of the turn's events get a later seq).
    /// </summary>
    public required long AssistantSeq { get; init; }

    /// <summary>
    /// The plan instant - the same clock read that stamped the placeholder
    /// row's <c>CreatedAt</c>, and the SHARED ANCHOR of the turn's two timers
    /// (chat-and-tools.md section detached turns): readers age the streaming row
    /// from it (<see cref="MessageStatusRules"/>, the orphan/409 horizon), and
    /// <see cref="TurnRunner"/> caps its lifetime at the REMAINING
    /// <see cref="TurnOptions.MaxTurnDuration"/> measured from it - so queue
    /// wait counts against the turn and the runner can never outlive the
    /// horizon readers enforce.
    /// </summary>
    public required DateTimeOffset PlannedAt { get; init; }

    public required string ModelId { get; init; }

    /// <summary>Prior turns (system prompt excluded), ending with the user message.</summary>
    public required IReadOnlyList<ChatModelMessage> History { get; init; }

    /// <summary>Offered tool capability ids (requested AND enabled AND entitled AND registry).</summary>
    public IReadOnlyList<string> ToolIds { get; init; } = [];

    /// <summary>Model-facing specs for the offered tools, same order as <see cref="ToolIds"/>.</summary>
    public IReadOnlyList<ChatToolSpec> Tools { get; init; } = [];

    /// <summary>The project's pinned instructions (step 0), or null.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>
    /// The caller's IANA timezone snapshot - the clock tool's default zone, so
    /// "what time is it" answers in the user's local time (null = UTC).
    /// </summary>
    public string? ClientTimezone { get; init; }
}
