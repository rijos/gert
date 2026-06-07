namespace Gert.Model.Events;

/// <summary>
/// <c>tool_result</c> — fills the tool card's result rows (rest-api.md SSE table).
/// </summary>
public sealed record ToolResultEvent : ChatEvent
{
    public required string Id { get; init; }

    /// <summary>The capability id of the tool that ran (e.g. <c>rag</c>).</summary>
    public required string Kind { get; init; }

    public required ToolCallStatus Status { get; init; }

    public long? LatencyMs { get; init; }

    /// <summary>Result hits/rows (e.g. doc-hit rows for a RAG call).</summary>
    public IReadOnlyList<ToolResultHit>? Hits { get; init; }

    /// <summary>Plain-text output the card renders verbatim (sandbox stdout, the clock reading).</summary>
    public string? Stdout { get; init; }

    /// <summary>The model-managed todo list (the <c>set_todos</c> tool) for the todo card.</summary>
    public IReadOnlyList<Chat.TodoItem>? Todos { get; init; }

    /// <summary>
    /// Human-readable failure text the card renders when <see cref="Status"/> is
    /// <see cref="ToolCallStatus.Error"/> — a timed-out call, a refused
    /// budget-exhausted call, a tool defect. Null on success.
    /// </summary>
    public string? Error { get; init; }

    public override ChatEventType Type => ChatEventType.ToolResult;
}
