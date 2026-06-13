namespace Gert.Model.Events;

/// <summary>
/// <c>message_start</c> - creates the assistant bubble (rest-api.md SSE table).
/// </summary>
public sealed record MessageStartEvent : ChatEvent
{
    public required string MessageId { get; init; }

    public override ChatEventType Type => ChatEventType.MessageStart;
}
