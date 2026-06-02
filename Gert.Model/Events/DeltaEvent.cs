namespace Gert.Model.Events;

/// <summary>
/// <c>delta</c> — a token-append for the typewriter effect (rest-api.md SSE table).
/// </summary>
public sealed record DeltaEvent : ChatEvent
{
    public required string Text { get; init; }

    public override ChatEventType Type => ChatEventType.Delta;
}
