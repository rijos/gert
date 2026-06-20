using Gert.Model;
using Gert.Model.Chat;
using Gert.Model.Events;

namespace Gert.Service.Chat;

/// <summary>
/// One entitled tool call the loop just executed - everything the driver's
/// <see cref="AgentLoopRequest.OnToolExecuted"/> callback needs to insert the
/// <c>tool_calls</c> row and collect that call's citations (bound to the row id).
/// The loop fills it from the <c>ToolOutcome</c> + the originating call; the
/// driver owns the row id (so it can bind citations to it) and the persistence.
/// </summary>
public sealed record ExecutedToolCall
{
    /// <summary>The model's call id (the <c>tool_call</c> event's id).</summary>
    public required string CallId { get; init; }

    /// <summary>The recorded <c>tool_calls.kind</c> (the tool's capability id).</summary>
    public required string Kind { get; init; }

    public required ToolCallStatus Status { get; init; }

    /// <summary>The raw tool arguments JSON (the row's request).</summary>
    public string? RequestJson { get; init; }

    /// <summary>The JSON fed back to the model (the row's response).</summary>
    public string? ResponseJson { get; init; }

    public long? LatencyMs { get; init; }

    /// <summary>The call's citations, before the driver binds them to the row id.</summary>
    public IReadOnlyList<Citation> Citations { get; init; } = [];

    /// <summary>Canvas artifacts the call created/updated (already persisted by the tool).</summary>
    public IReadOnlyList<Artifact>? Artifacts { get; init; }
}
