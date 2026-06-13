namespace Gert.Api.WebSockets;

/// <summary>
/// Client->server WS messages (rest-api.md section the ws endpoint): JSON with a
/// <c>type</c> discriminator, e.g. <c>{"type":"subscribe","after":42}</c>.
/// Parsed by <see cref="ClientMessageParser"/>; dispatched by
/// <see cref="MessageHandlerRegistry"/>. Unknown/malformed messages are ignored
/// - never a reason to tear down the socket.
/// </summary>
public abstract record ClientMessage
{
    /// <summary>Subscribe to live events after a cursor (replay-then-live splice).</summary>
    public sealed record Subscribe(long After) : ClientMessage;

    /// <summary>Request one page of the event log over the socket (history backfill).</summary>
    public sealed record Range(long After, int Limit) : ClientMessage;

    /// <summary>Stop the in-flight turn of this socket's conversation.</summary>
    public sealed record Cancel : ClientMessage;
}
