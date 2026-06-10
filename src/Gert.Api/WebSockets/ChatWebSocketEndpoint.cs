using System.Net.WebSockets;
using Gert.Api.Validation;

namespace Gert.Api.WebSockets;

/// <summary>
/// <c>GET /api/projects/{pid}/conversations/{id}/ws</c> — the WS delivery
/// transport (rest-api.md § the ws endpoint).
///
/// <para>
/// <b>Auth (security F2):</b> the client offers the token as the second
/// <c>Sec-WebSocket-Protocol</c> entry
/// (<c>new WebSocket(url, ["bearer", token])</c>);
/// <see cref="WsBearerSubprotocolMiddleware"/> lifts it into the Authorization
/// header BEFORE authentication, so this endpoint is guarded by the exact same
/// JwtBearer validation + fallback authorization policy as the rest of the API
/// — an unauthenticated upgrade never reaches this handler. On accept the
/// <c>"bearer"</c> subprotocol is echoed when the client offered it.
/// </para>
///
/// <para>
/// After accept, the receive loop parses client messages with the safe
/// <see cref="ClientMessageParser"/> and dispatches via
/// <see cref="MessageHandlerRegistry"/> — unknown/malformed messages are
/// ignored; only transport events end the session.
/// </para>
/// </summary>
public static class ChatWebSocketEndpoint
{
    /// <summary>The subprotocol name a browser client offers first.</summary>
    public const string BearerProtocol = "bearer";

    /// <summary>Cap on one client message; bigger frames close the socket.</summary>
    private const int MaxMessageBytes = 64 * 1024;

    /// <summary>Map the WS route (fallback authenticated-user policy applies).</summary>
    public static IEndpointConventionBuilder MapChatWebSocket(this IEndpointRouteBuilder app) =>
        app.MapGet("api/projects/{pid}/conversations/{id}/ws", HandleAsync);

    private static async Task HandleAsync(string pid, string id, HttpContext context)
    {
        // {pid} shape guard (configuration.md § 2.5) BEFORE the socket is accepted:
        // the thrown ValidationException reaches UseExceptionHandler →
        // ValidationExceptionHandler, which renders the same branded 400
        // ProblemDetails the sibling controllers produce — the upgrade response has
        // not started yet, so a malformed pid never becomes an accepted-then-closed
        // socket.
        RouteParams.RequireValidProjectId(pid);

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // Echo "bearer" when the browser offered it (RFC 6455: the server must
        // select an offered subprotocol); a non-browser client that sent a plain
        // Authorization header offers none.
        var offeredBearer = context.WebSockets.WebSocketRequestedProtocols
            .Any(p => string.Equals(p.Trim(), BearerProtocol, StringComparison.Ordinal));

        using var socket = await context.WebSockets
            .AcceptWebSocketAsync(offeredBearer ? BearerProtocol : null)
            .ConfigureAwait(false);
        await using var session = new ChatSocketSession(socket, pid, id, context.RequestServices);

        var registry = context.RequestServices.GetRequiredService<MessageHandlerRegistry>();
        await ReceiveLoopAsync(session, registry, context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task ReceiveLoopAsync(
        ChatSocketSession session,
        MessageHandlerRegistry registry,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8 * 1024];
        using var message = new MemoryStream();

        try
        {
            while (session.Socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                message.SetLength(0);

                // Accumulate one full message (frames may be fragmented).
                WebSocketReceiveResult result;
                do
                {
                    result = await session.Socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await session.Socket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure, null, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                    if (message.Length > MaxMessageBytes)
                    {
                        await session.Socket.CloseAsync(
                            WebSocketCloseStatus.MessageTooBig, "message too big", cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue; // binary frames are not part of the protocol — ignore.
                }

                var parsed = ClientMessageParser.Parse(message.GetBuffer().AsSpan(0, (int)message.Length));
                if (parsed is null)
                {
                    continue; // malformed/unknown — never tears down the socket.
                }

                await registry.DispatchAsync(session, parsed, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Request aborted (client gone / shutdown) — normal teardown.
        }
        catch (WebSocketException)
        {
            // Abrupt peer close — normal teardown.
        }
    }
}
