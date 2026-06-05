using Gert.Service.Chat;

namespace Gert.Api.WebSockets;

/// <summary>
/// Dispatch table for client WS messages (rest-api.md § the ws endpoint): one
/// handler per <see cref="ClientMessage"/> shape, with the defaults registered
/// up front — <c>subscribe</c> (replay-then-live) and <c>range</c> (history
/// backfill over the socket). Singleton; per-connection state lives on the
/// <see cref="ChatSocketSession"/>, per-request services on its
/// <see cref="ChatSocketSession.Services"/>. Future message kinds (e.g.
/// <c>cancel</c> for client-initiated turn abort) register here.
/// </summary>
public sealed class MessageHandlerRegistry
{
    private readonly Dictionary<Type, Func<ChatSocketSession, ClientMessage, CancellationToken, Task>> _handlers = new();

    /// <summary>Build the registry with the default handlers.</summary>
    public MessageHandlerRegistry()
    {
        Register<ClientMessage.Subscribe>(static (session, message, token) =>
            session.ResubscribeAsync(message.After, token));

        Register<ClientMessage.Range>(static async (session, message, token) =>
        {
            var reader = session.Services.GetRequiredService<IConversationReader>();
            var range = await reader
                .ReadRangeAsync(session.Pid, session.ConversationId, message.After, message.Limit, token)
                .ConfigureAwait(false);

            await session.SendAsync(
                new { kind = "range", events = range.Events, next_cursor = range.NextCursor, has_more = range.HasMore },
                token).ConfigureAwait(false);
        });
    }

    /// <summary>Register (or replace) the handler for one message shape.</summary>
    public void Register<TMessage>(Func<ChatSocketSession, TMessage, CancellationToken, Task> handler)
        where TMessage : ClientMessage
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[typeof(TMessage)] = (session, message, token) => handler(session, (TMessage)message, token);
    }

    /// <summary>Dispatch one parsed message; unhandled shapes are ignored.</summary>
    public Task DispatchAsync(ChatSocketSession session, ClientMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(message);

        return _handlers.TryGetValue(message.GetType(), out var handler)
            ? handler(session, message, cancellationToken)
            : Task.CompletedTask;
    }
}
