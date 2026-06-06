namespace Gert.Service.External;

/// <summary>One message in the model conversation sent upstream.</summary>
public sealed record ChatModelMessage
{
    /// <summary>OpenAI-style role: <c>system</c> | <c>user</c> | <c>assistant</c> | <c>tool</c>.</summary>
    public required string Role { get; init; }

    /// <summary>Message text; <c>null</c> for an assistant turn that only carries tool calls.</summary>
    public string? Content { get; init; }

    /// <summary>For tool-result messages (<c>role:"tool"</c>): the id of the tool call this responds to.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>For assistant turns that requested tools: the calls of that round, in order.</summary>
    public IReadOnlyList<ChatModelToolCall>? ToolCalls { get; init; }

    /// <summary>
    /// Prior-turn thinking text sent back upstream (Qwen3.6 interleaved
    /// thinking): the template re-wraps it as a <c>&lt;think&gt;</c> block when
    /// <c>preserve_thinking</c> is on. Only set on assistant history messages.
    /// </summary>
    public string? ReasoningContent { get; init; }
}
