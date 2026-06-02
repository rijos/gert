namespace Gert.Service.External;

/// <summary>
/// One streamed chunk — either a text delta or a tool-call request. A null
/// <see cref="TextDelta"/> with a set <see cref="ToolCall"/> is a tool call; the
/// terminal chunk carries <see cref="FinishReason"/> and optionally a token count.
/// </summary>
public sealed record ChatModelChunk
{
    public string? TextDelta { get; init; }

    public ChatModelToolCall? ToolCall { get; init; }

    /// <summary>Set on the final chunk (e.g. <c>stop</c>, <c>tool_calls</c>, <c>length</c>).</summary>
    public string? FinishReason { get; init; }

    public int? TokenCount { get; init; }
}
