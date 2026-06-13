namespace Gert.Model.Events;

/// <summary>
/// <c>reasoning</c> - a coalesced thinking-text delta (vLLM
/// <c>reasoning_content</c>). Streams before the answer's <c>delta</c> events;
/// the SPA renders it in the collapsed "Thinking" block.
/// </summary>
public sealed record ReasoningEvent : ChatEvent
{
    public required string Text { get; init; }

    public override ChatEventType Type => ChatEventType.Reasoning;
}
