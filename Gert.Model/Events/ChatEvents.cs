namespace Gert.Model.Events;

/// <summary>
/// <c>message_start</c> — creates the assistant bubble (rest-api.md SSE table).
/// </summary>
public sealed record MessageStartEvent : ChatEvent
{
    public required string MessageId { get; init; }

    public override ChatEventType Type => ChatEventType.MessageStart;
}

/// <summary>
/// <c>tool_call</c> — a tool card with the spinner appears. The
/// <see cref="Request"/> is the opaque tool input (e.g. <c>{"query":"…"}</c>).
/// </summary>
public sealed record ToolCallEvent : ChatEvent
{
    public required string Id { get; init; }

    /// <summary>The capability id of the tool being called (e.g. <c>rag</c>).</summary>
    public required string Kind { get; init; }

    public required ToolCallStatus Status { get; init; }

    /// <summary>The tool's request payload (e.g. the search query / code).</summary>
    public IReadOnlyDictionary<string, object?>? Request { get; init; }

    public override ChatEventType Type => ChatEventType.ToolCall;
}

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

/// <summary>
/// A single hit in a <see cref="ToolResultEvent"/> — e.g. the
/// <c>{"doc","page","score"}</c> rows the RAG card renders (rest-api.md SSE table).
/// </summary>
public sealed record ToolResultHit
{
    public string? Doc { get; init; }

    public string? Page { get; init; }

    public double? Score { get; init; }

    /// <summary>Web-result title (for web_search hits).</summary>
    public string? Title { get; init; }

    public string? Url { get; init; }
}

/// <summary>
/// <c>delta</c> — a token-append for the typewriter effect (rest-api.md SSE table).
/// </summary>
public sealed record DeltaEvent : ChatEvent
{
    public required string Text { get; init; }

    public override ChatEventType Type => ChatEventType.Delta;
}

/// <summary>
/// <c>citation</c> — the <c>[n]</c> marker + footnote (rest-api.md SSE table).
/// </summary>
public sealed record CitationEvent : ChatEvent
{
    public required int Ordinal { get; init; }

    public required string Label { get; init; }

    public string? DocId { get; init; }

    public override ChatEventType Type => ChatEventType.Citation;
}

/// <summary>
/// <c>artifact</c> — opens a canvas tab (rest-api.md SSE table).
/// </summary>
public sealed record ArtifactEvent : ChatEvent
{
    public required string Id { get; init; }

    public required ArtifactKind Kind { get; init; }

    public required string Name { get; init; }

    public required string Content { get; init; }

    public override ChatEventType Type => ChatEventType.Artifact;
}

/// <summary>
/// <c>message_end</c> — removes the caret; carries the final token count
/// (rest-api.md SSE table).
/// </summary>
public sealed record MessageEndEvent : ChatEvent
{
    public int? TokenCount { get; init; }

    public override ChatEventType Type => ChatEventType.MessageEnd;
}

/// <summary>
/// <c>error</c> — an inline error in the stream (rest-api.md SSE table).
/// </summary>
public sealed record ErrorEvent : ChatEvent
{
    public required string Message { get; init; }

    public override ChatEventType Type => ChatEventType.Error;
}
