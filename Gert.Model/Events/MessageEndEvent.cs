namespace Gert.Model.Events;

/// <summary>
/// <c>message_end</c> — removes the caret; carries the final token count
/// (rest-api.md SSE table).
/// </summary>
public sealed record MessageEndEvent : ChatEvent
{
    public int? TokenCount { get; init; }

    public override ChatEventType Type => ChatEventType.MessageEnd;
}
