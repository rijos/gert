using System.Net.WebSockets;
using System.Text.Json;
using Gert.Model.Events;
using Gert.Model.Json;
using Gert.Service.Chat;

namespace Gert.Api.WebSockets;

/// <summary>
/// One accepted chat WebSocket (rest-api.md section the ws endpoint). Owns the
/// connection state the message handlers act on: the socket, the conversation
/// scope, the per-connection send lock (the live pump and range replies share
/// one socket), and the current live subscription (a new <c>subscribe</c>
/// replaces the previous one). Scoped to the connection - its
/// <see cref="Services"/> are the upgrade request's scoped services, which live
/// for the lifetime of the socket.
/// </summary>
public sealed class ChatSocketSession : IAsyncDisposable
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource? _liveCts;
    private Task? _livePump;

    public ChatSocketSession(WebSocket socket, string pid, string conversationId, IServiceProvider services)
    {
        Socket = socket ?? throw new ArgumentNullException(nameof(socket));
        Pid = pid ?? throw new ArgumentNullException(nameof(pid));
        ConversationId = conversationId ?? throw new ArgumentNullException(nameof(conversationId));
        Services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public WebSocket Socket { get; }

    public string Pid { get; }

    public string ConversationId { get; }

    /// <summary>The connection's scoped services (streamer, reader, ...).</summary>
    public IServiceProvider Services { get; }

    /// <summary>Serialize <paramref name="frame"/> (wire contract) and send it whole.</summary>
    public async Task SendAsync(object frame, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, GertJsonOptions.Default);

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>One event frame: <c>{"kind":"event","seq":n,"event":{...}}</c>.</summary>
    public Task SendEventAsync(TurnEvent turnEvent, CancellationToken cancellationToken) =>
        SendAsync(new { kind = "event", seq = turnEvent.Seq, @event = turnEvent.Event }, cancellationToken);

    /// <summary>
    /// Start (or replace) the live pump: the replay-then-live splice from
    /// <paramref name="after"/>, each event sent as a frame. The previous
    /// subscription, if any, is cancelled - one live cursor per socket.
    /// </summary>
    public async Task ResubscribeAsync(long after, CancellationToken connectionToken)
    {
        await StopLivePumpAsync().ConfigureAwait(false);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
        _liveCts = cts;
        _livePump = PumpAsync(after, cts.Token);
    }

    private async Task PumpAsync(long after, CancellationToken token)
    {
        try
        {
            var streamer = Services.GetRequiredService<IConversationStreamer>();
            await foreach (var turnEvent in streamer.StreamAsync(Pid, ConversationId, after, token).ConfigureAwait(false))
            {
                await SendEventAsync(turnEvent, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Resubscribe/close - expected.
        }
        catch (WebSocketException)
        {
            // The peer went away mid-send; the receive loop observes the close.
        }
        catch (Exception ex)
        {
            // Surface a non-fatal error frame (e.g. a corrupt log row); the
            // client may re-subscribe or fall back to the range endpoint. Like
            // TurnRunner's catch-all (style guide section 7), the frame carries a
            // generic message, never raw ex.Message - exception text can echo
            // internal URLs or prompt fragments; the detail goes to the log.
            Services.GetRequiredService<ILogger<ChatSocketSession>>().LogError(
                ex,
                "Live pump faulted unexpectedly for conversation {ConversationId} in project {Pid}.",
                ConversationId, Pid);
            await TrySendErrorAsync("Something went wrong streaming this conversation.").ConfigureAwait(false);
        }
    }

    private async Task TrySendErrorAsync(string message)
    {
        try
        {
            await SendAsync(new { kind = "error", message }, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Socket already unusable - the receive loop is tearing down.
        }
    }

    private async Task StopLivePumpAsync()
    {
        if (_liveCts is not null)
        {
            await _liveCts.CancelAsync().ConfigureAwait(false);
        }

        if (_livePump is not null)
        {
            try
            {
                await _livePump.ConfigureAwait(false);
            }
            catch
            {
                // Pump failures were already surfaced as error frames.
            }
        }

        _liveCts?.Dispose();
        _liveCts = null;
        _livePump = null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopLivePumpAsync().ConfigureAwait(false);
        _sendLock.Dispose();
    }
}
