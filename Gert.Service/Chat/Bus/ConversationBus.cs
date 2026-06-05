using System.Collections.Concurrent;
using System.Threading.Channels;
using Gert.Model.Events;

namespace Gert.Service.Chat.Bus;

/// <summary>
/// Singleton <see cref="IConversationBus"/>: a <see cref="ConcurrentDictionary"/>
/// of topic → subscriber set, each subscriber a <b>bounded</b> channel
/// (chat-and-tools.md § detached turns). Publish is fire-and-forget per
/// subscriber (<c>TryWrite</c>): a full buffer means the consumer is too slow or
/// gone, so the subscriber is dropped — its channel completes, the transport
/// notices, and the client resumes via the DB range read. The runner is never
/// throttled by delivery.
/// </summary>
public sealed class ConversationBus : IConversationBus
{
    /// <summary>Per-subscriber buffer; deltas are small, this is seconds of slack.</summary>
    private const int SubscriberCapacity = 256;

    private readonly ConcurrentDictionary<
        ConversationTopic,
        ConcurrentDictionary<Guid, Subscription>> _topics = new();

    /// <inheritdoc />
    public IConversationSubscription Subscribe(ConversationTopic topic)
    {
        var subscribers = _topics.GetOrAdd(
            topic,
            static _ => new ConcurrentDictionary<Guid, Subscription>());

        var subscription = new Subscription(this, topic);
        subscribers[subscription.Id] = subscription;

        // Lost race: another thread removed the (then-empty) set after our GetOrAdd.
        // Re-adding under the same topic key keeps the subscription reachable.
        if (!ReferenceEquals(_topics.GetOrAdd(topic, subscribers), subscribers))
        {
            _topics.GetOrAdd(topic, static _ => new ConcurrentDictionary<Guid, Subscription>())
                [subscription.Id] = subscription;
        }

        return subscription;
    }

    /// <inheritdoc />
    public void Publish(ConversationTopic topic, TurnEvent turnEvent)
    {
        ArgumentNullException.ThrowIfNull(turnEvent);

        if (!_topics.TryGetValue(topic, out var subscribers))
        {
            return; // nobody watching — the DB log is the source of truth anyway.
        }

        foreach (var subscription in subscribers.Values)
        {
            if (!subscription.TryDeliver(turnEvent))
            {
                // Buffer full → slow/dead consumer. Drop it instead of stalling the
                // turn; completion of its reader tells the transport to fall back
                // to a DB catch-up.
                subscription.Dispose();
            }
        }
    }

    private void Remove(ConversationTopic topic, Guid id)
    {
        if (_topics.TryGetValue(topic, out var subscribers))
        {
            subscribers.TryRemove(id, out _);
            if (subscribers.IsEmpty)
            {
                // Best-effort cleanup of empty topics; a racing Subscribe re-adds.
                _topics.TryRemove(new KeyValuePair<ConversationTopic, ConcurrentDictionary<Guid, Subscription>>(topic, subscribers));
            }
        }
    }

    private sealed class Subscription : IConversationSubscription
    {
        private readonly ConversationBus _bus;
        private readonly ConversationTopic _topic;
        private readonly Channel<TurnEvent> _channel;
        private int _disposed;

        internal Subscription(ConversationBus bus, ConversationTopic topic)
        {
            _bus = bus;
            _topic = topic;
            _channel = Channel.CreateBounded<TurnEvent>(new BoundedChannelOptions(SubscriberCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait, // TryWrite returns false when full
            });
        }

        internal Guid Id { get; } = Guid.NewGuid();

        public ChannelReader<TurnEvent> Reader => _channel.Reader;

        internal bool TryDeliver(TurnEvent turnEvent) => _channel.Writer.TryWrite(turnEvent);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _bus.Remove(_topic, Id);
            _channel.Writer.TryComplete();
        }
    }
}
