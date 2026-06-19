using FluentAssertions;
using Gert.Model.Events;
using Gert.Service.Chat.Bus;
using Xunit;

namespace Gert.Service.Tests;

/// <summary>
/// The in-process pub/sub: delivery/order, topic isolation (full identity scope),
/// unsubscribe semantics, and the never-block-the-publisher overflow rule.
/// </summary>
public sealed class ConversationBusTests
{
    private static readonly ConversationTopic Topic = new("https://idp.example", "sub-123", "pid-1", "conv-1");

    private static TurnEvent Evt(long seq) => new()
    {
        Seq = seq,
        Event = new DeltaEvent { Text = $"chunk-{seq}" },
    };

    [Fact]
    public async Task Subscriber_receives_published_events_in_order()
    {
        var bus = new ConversationBus();
        using var subscription = bus.Subscribe(Topic);

        bus.Publish(Topic, Evt(1));
        bus.Publish(Topic, Evt(2));
        bus.Publish(Topic, Evt(3));

        var received = new List<long>();
        while (received.Count < 3)
        {
            var evt = await subscription.Reader.ReadAsync(TestContext.Current.CancellationToken);
            received.Add(evt.Seq);
        }

        received.Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Publish_without_subscribers_is_a_noop()
    {
        var bus = new ConversationBus();
        var act = () => bus.Publish(Topic, Evt(1));
        act.Should().NotThrow();
    }

    [Fact]
    public async Task Both_subscribers_receive_every_event()
    {
        var bus = new ConversationBus();
        using var first = bus.Subscribe(Topic);
        using var second = bus.Subscribe(Topic);

        bus.Publish(Topic, Evt(1));

        (await first.Reader.ReadAsync(TestContext.Current.CancellationToken)).Seq.Should().Be(1);
        (await second.Reader.ReadAsync(TestContext.Current.CancellationToken)).Seq.Should().Be(1);
    }

    [Fact]
    public void Topics_are_isolated_by_full_identity_scope()
    {
        var bus = new ConversationBus();

        // Same conversation id, different user - must not receive (the topic is
        // scoped like the database path: iss/sub/pid/conversation).
        var otherUser = Topic with { Sub = "sub-456" };
        using var subscription = bus.Subscribe(otherUser);

        bus.Publish(Topic, Evt(1));

        subscription.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public void Disposed_subscription_receives_nothing_and_completes()
    {
        var bus = new ConversationBus();
        var subscription = bus.Subscribe(Topic);
        subscription.Dispose();

        bus.Publish(Topic, Evt(1));

        subscription.Reader.TryRead(out _).Should().BeFalse();
        subscription.Reader.Completion.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task Slow_subscriber_is_dropped_instead_of_blocking_the_publisher()
    {
        var bus = new ConversationBus();
        using var slow = bus.Subscribe(Topic);
        using var healthy = bus.Subscribe(Topic);

        // Overflow the slow subscriber's bounded buffer (capacity 256) without
        // reading; the publisher must never block and must drop the overflowing
        // subscriber rather than stall the turn.
        for (var seq = 1; seq <= 300; seq++)
        {
            bus.Publish(Topic, Evt(seq));

            healthy.Reader.TryRead(out _).Should().BeTrue();
        }

        // The drop is per-subscriber: the healthy one still receives...
        bus.Publish(Topic, Evt(301));
        healthy.Reader.TryRead(out var evt).Should().BeTrue();
        evt!.Seq.Should().Be(301);

        // ...while the slow one's stream ENDS (drains its buffer, then completes -
        // Completion is deferred until the buffer empties): nothing past the
        // buffered prefix, never event 301.
        var drained = new List<long>();
        await foreach (var buffered in slow.Reader.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            drained.Add(buffered.Seq);
        }

        drained.Should().HaveCount(256).And.NotContain(301);
        slow.Reader.Completion.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task Dropped_subscriber_can_drain_its_buffer_before_completion()
    {
        var bus = new ConversationBus();
        using var subscription = bus.Subscribe(Topic);

        for (var seq = 1; seq <= 300; seq++)
        {
            bus.Publish(Topic, Evt(seq));
        }

        // Buffer holds the first 256; completion follows the drain - nothing is
        // silently lost below the high-water mark, and the consumer then re-splices.
        var drained = new List<long>();
        await foreach (var evt in subscription.Reader.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            drained.Add(evt.Seq);
        }

        drained.Should().Equal(Enumerable.Range(1, 256).Select(i => (long)i));
    }
}
