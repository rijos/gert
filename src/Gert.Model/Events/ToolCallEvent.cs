using Gert.Model.Chat;

namespace Gert.Model.Events;

/// <summary>
/// <c>tool_call</c> - a tool card with the spinner appears. The
/// <see cref="Request"/> is the opaque tool input (e.g. <c>{"query":"..."}</c>).
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
