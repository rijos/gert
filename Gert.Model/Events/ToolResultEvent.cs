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

    public override ChatEventType Type => ChatEventType.ToolResult;
}
