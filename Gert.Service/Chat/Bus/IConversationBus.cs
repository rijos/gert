using System.Threading.Channels;
using Gert.Model.Events;

namespace Gert.Service.Chat.Bus;

/// <summary>
/// The in-process pub/sub for live turn delivery (chat-and-tools.md § detached
/// turns). A per-process latency optimization only: the turn runner publishes
/// every event here <i>after</i> persisting it to <c>turn_events</c>, so a
/// subscriber on another instance (or one that missed events) is still correct
/// via the DB catch-up — losing a bus message can never lose data.
/// </summary>
public interface IConversationBus
{
    /// <summary>
    /// Subscribe to a conversation's live events. Dispose to unsubscribe. Events
    /// published after the call are readable from
    /// <see cref="IConversationSubscription.Reader"/>; subscribe BEFORE reading
    /// the DB catch-up and dedup by seq to splice replay and live without gaps.
    /// </summary>
    IConversationSubscription Subscribe(ConversationTopic topic);

    /// <summary>
    /// Publish to all current subscribers, never blocking the publisher: a
    /// subscriber whose buffer is full is dropped (its channel completes) rather
    /// than stalling the turn — it reconnects and catches up from the DB.
    /// </summary>
    void Publish(ConversationTopic topic, TurnEvent turnEvent);
}

/// <summary>A live subscription; dispose to unsubscribe and complete the reader.</summary>
public interface IConversationSubscription : IDisposable
{
    /// <summary>The live event feed. Completes when dropped or disposed.</summary>
    ChannelReader<TurnEvent> Reader { get; }
}
