namespace Gert.Tools;

/// <summary>
/// One tool call from the model - the active project and the raw JSON arguments
/// (e.g. <c>{"query":"...","k":8}</c>).
/// </summary>
public sealed record ToolInvocation
{
    public required string Pid { get; init; }

    /// <summary>Raw tool arguments as a JSON string.</summary>
    public required string ArgumentsJson { get; init; }

    /// <summary>
    /// The conversation this call runs in. Optional so callers that don't need it
    /// (most tools) keep their construction unchanged; the artifact tools require
    /// it to scope/persist canvas artifacts and error without it.
    /// </summary>
    public string? ConversationId { get; init; }

    /// <summary>The assistant message producing this call (artifact provenance).</summary>
    public string? MessageId { get; init; }

    /// <summary>
    /// The model's id for THIS call (the <c>tool_call</c> event's <c>Id</c>), so a
    /// mid-execution event a tool emits through <see cref="IToolHost.Ui"/> can fold
    /// onto the same card. Null for callers outside the turn loop.
    /// </summary>
    public string? ToolCallId { get; init; }

    /// <summary>
    /// The turn's wall-clock deadline (<c>PlannedAt + MaxTurnDuration</c>, the
    /// runner's lifetime anchor - chat-and-tools.md section detached turns). A
    /// long-waiting modal tool (<c>ToolType.Modal</c>) budgets its wait
    /// against this so its graceful timeout always beats the turn-budget error
    /// finalize. Null outside the turn loop.
    /// </summary>
    public DateTimeOffset? Deadline { get; init; }

    /// <summary>
    /// The caller's IANA timezone (the send request's snapshot) - the clock
    /// tool's default zone. Null means UTC.
    /// </summary>
    public string? ClientTimezone { get; init; }

    /// <summary>
    /// The parent turn's provider id (<c>TurnJob.ModelId</c>), so a delegating
    /// tool (<c>run_sub_agent</c>) talks to the same model. Null outside
    /// the turn loop - delegation is then unavailable.
    /// </summary>
    public string? ModelId { get; init; }

    /// <summary>
    /// The parent turn's entitlement snapshot (<c>TurnJob.AllowedToolIds</c>).
    /// A tool that runs other tools intersects with this so the claim stays the
    /// ceiling (auth.md) at every nesting depth. Null (outside the turn loop)
    /// means no nested tools at all - fail closed.
    /// </summary>
    public IReadOnlySet<string>? AllowedToolIds { get; init; }
}
