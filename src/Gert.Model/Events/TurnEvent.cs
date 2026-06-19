namespace Gert.Model.Events;

/// <summary>
/// The delivery envelope around a <see cref="ChatEvent"/>: the event plus its
/// per-conversation monotonic <see cref="Seq"/> (chat-and-tools.md section detached
/// turns). The bus, the SSE stream (<c>id:</c> field) and the
/// range endpoint both carry this envelope; <c>seq</c> is the one cursor for
/// pagination, catch-up, and resume. The inner <see cref="ChatEvent"/> union and
/// its wire names are unchanged.
/// </summary>
public sealed record TurnEvent
{
    /// <summary>Per-conversation monotonic sequence of this event.</summary>
    public required long Seq { get; init; }

    public required ChatEvent Event { get; init; }
}
