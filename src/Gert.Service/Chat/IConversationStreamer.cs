using Gert.Model.Events;

namespace Gert.Service.Chat;

/// <summary>
/// The replay-then-live splice (chat-and-tools.md section detached turns): one
/// gap-free, dup-free <see cref="TurnEvent"/> stream from a cursor, regardless
/// of whether events are already durable or still being produced. Both delivery
/// paths (the SSE stream and range polling) consume this - the splice logic
/// exists exactly once.
/// </summary>
public interface IConversationStreamer
{
    /// <summary>
    /// Stream events with <c>seq &gt; afterSeq</c>: DB catch-up first, then live
    /// bus events, deduplicated by seq watermark. Runs until cancelled; if the
    /// bus drops the subscription (slow consumer), it transparently re-splices
    /// from the watermark.
    /// </summary>
    IAsyncEnumerable<TurnEvent> StreamAsync(
        string pid,
        string conversationId,
        long afterSeq,
        CancellationToken cancellationToken = default);
}
