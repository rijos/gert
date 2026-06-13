namespace Gert.Model.Events;

/// <summary>
/// <c>error</c> - an inline error in the stream (rest-api.md SSE table).
/// </summary>
public sealed record ErrorEvent : ChatEvent
{
    public required string Message { get; init; }

    public override ChatEventType Type => ChatEventType.Error;
}
