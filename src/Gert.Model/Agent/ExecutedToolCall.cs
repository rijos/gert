using Gert.Model.Chat;
using Gert.Model.Events;

namespace Gert.Model.Agent;

/// <summary>
/// One entitled tool call the agent loop executed - the complete record a consumer
/// needs to render the result card AND persist the <c>tool_calls</c> row with its
/// citations (bound to the row id) and canvas artifacts. The loop fills it from the
/// executed call; the caller (the event-log tee) owns the row id and the persistence.
/// Carried by <see cref="ToolCompleted"/>.
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

    /// <summary>The call's citations, before the caller binds them to the row id.</summary>
    public IReadOnlyList<Citation> Citations { get; init; } = [];

    /// <summary>Canvas artifacts the call created/updated (already persisted by the tool).</summary>
    public IReadOnlyList<Artifact>? Artifacts { get; init; }

    /// <summary>Result hits/rows the card renders (e.g. doc-hit rows for a RAG call).</summary>
    public IReadOnlyList<ToolResultHit>? Hits { get; init; }

    /// <summary>Plain-text card output (sandbox stdout, the clock reading).</summary>
    public string? Stdout { get; init; }

    /// <summary>The model-managed todo list (the <c>set_todos</c> tool) for the todo card.</summary>
    public IReadOnlyList<TodoItem>? Todos { get; init; }

    /// <summary>Human-readable failure text for the card when <see cref="Status"/> is error; null on success.</summary>
    public string? Error { get; init; }
}
