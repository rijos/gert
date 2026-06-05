namespace Gert.Model.Events;

/// <summary>
/// The delivery envelope around a <see cref="ChatEvent"/>: the event plus its
/// per-conversation monotonic <see cref="Seq"/> (chat-and-tools.md § detached
/// turns). The bus, the SSE stream (<c>id:</c> field), the WS frames, and the
/// range endpoint all carry this envelope; <c>seq</c> is the one cursor for
/// pagination, catch-up, and resume. The inner <see cref="ChatEvent"/> union and
/// its wire names are unchanged.
/// </summary>
public sealed record TurnEvent
{
    /// <summary>Per-conversation monotonic sequence of this event.</summary>
    public required long Seq { get; init; }

    /// <summary>The chat event payload (the unchanged polymorphic union).</summary>
    public required ChatEvent Event { get; init; }
}
