namespace Gert.Service.External;

/// <summary>
/// One streamed chunk — a text/reasoning delta or a tool-call request. A null
/// <see cref="TextDelta"/> with a set <see cref="ToolCall"/> is a tool call; the
/// terminal chunk carries <see cref="FinishReason"/>; token counts ride the
/// terminal chunk or a trailing usage-only chunk (vLLM sends usage last).
/// </summary>
public sealed record ChatModelChunk
{
    public string? TextDelta { get; init; }

    /// <summary>Thinking text (vLLM <c>delta.reasoning_content</c>); precedes content.</summary>
    public string? ReasoningDelta { get; init; }

    public ChatModelToolCall? ToolCall { get; init; }

    /// <summary>
    /// Set when a tool call's name first appears mid-stream, before its arguments
    /// finish — lets the orchestrator surface intent live. The full call follows
    /// via <see cref="ToolCall"/> with the same id.
    /// </summary>
    public ToolCallStart? ToolCallStart { get; init; }

    /// <summary>Set on the final chunk (e.g. <c>stop</c>, <c>tool_calls</c>, <c>length</c>).</summary>
    public string? FinishReason { get; init; }

    /// <summary>Completion tokens (usage.completion_tokens).</summary>
    public int? TokenCount { get; init; }

    /// <summary>Prompt tokens of the round (usage.prompt_tokens).</summary>
    public int? PromptTokenCount { get; init; }
}
