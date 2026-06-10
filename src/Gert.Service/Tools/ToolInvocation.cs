using Gert.Model.Events;

namespace Gert.Service.Tools;

/// <summary>
/// One tool call from the model — the active project and the raw JSON arguments
/// (e.g. <c>{"query":"…","k":8}</c>).
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
    /// The model's id for THIS call (the <c>tool_call</c> event's <c>Id</c>), so
    /// a mid-execution event a tool emits can fold onto the same card. Null for
    /// callers outside the turn loop.
    /// </summary>
    public string? ToolCallId { get; init; }

    /// <summary>
    /// Mid-execution event channel — the runner's persist-then-publish
    /// <c>EmitAsync</c> (seq → durable log → bus), so a tool-emitted event
    /// replays exactly like any other. Null for hosts that don't stream; tools
    /// must tolerate null (<c>AskUserTool</c> errors rather than waiting
    /// invisibly).
    /// </summary>
    public Func<ChatEvent, CancellationToken, Task>? EmitAsync { get; init; }

    /// <summary>
    /// The turn's wall-clock deadline (<c>PlannedAt + MaxTurnDuration</c>, the
    /// runner's lifetime anchor — chat-and-tools.md § detached turns). A
    /// long-waiting tool (<see cref="IInteractiveTool"/>) budgets its wait
    /// against this so its graceful timeout always beats the turn-budget error
    /// finalize. Null outside the turn loop.
    /// </summary>
    public DateTimeOffset? Deadline { get; init; }
}
