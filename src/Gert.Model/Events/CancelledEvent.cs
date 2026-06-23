namespace Gert.Model.Events;

/// <summary>
/// Terminal event for a user-stopped turn: the assistant row finalised as
/// <c>cancelled</c> with whatever content streamed before the stop. Distinct
/// from <see cref="ErrorEvent"/> - a stop is a normal outcome, not a fault.
/// </summary>
public sealed record CancelledEvent : ChatEvent
{
    /// <summary>Completion token count at the point the turn was stopped; null if none reported.</summary>
    public int? TokenCount { get; init; }

    public override ChatEventType Type => ChatEventType.Cancelled;
}
