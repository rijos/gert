using FluentAssertions;
using Gert.Model.Dtos;
using Gert.Model.Events;
using Gert.Service.Chat;
using Gert.Service.Chat.Bus;
using NSubstitute;
using Xunit;

namespace Gert.Service.Tests;

/// <summary>
/// The replay-then-live splice — the correctness seam of the detached pipeline.
/// A real <see cref="ConversationBus"/> with an in-memory "durable log" behind a
/// substituted <see cref="IConversationReader"/>: tests drive the runner's
/// persist-then-publish protocol by hand and assert the spliced stream has no
/// gaps and no duplicates.
/// </summary>
public sealed class ConversationStreamerTests
{
    private const string Pid = "pid-1";
    private const string Conv = "conv-1";

    private readonly ConversationBus _bus = new();
    private readonly IConversationReader _reader = Substitute.For<IConversationReader>();
    private readonly TestUserContext _user = new();
    private readonly List<TurnEvent> _log = [];
    private readonly ConversationTopic _topic;

    public ConversationStreamerTests()
    {
        _topic = new ConversationTopic(_user.Iss, _user.Sub, Pid, Conv);

        // The substituted reader serves pages from the in-memory log with the
        // real cursor semantics (seq > after, ascending, limit, HasMore).
        _reader.ReadRangeAsync(Pid, Conv, Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var after = ci.ArgAt<long>(2);
                var limit = ci.ArgAt<int>(3);
                var matching = _log.Where(e => e.Seq > after).OrderBy(e => e.Seq).ToList();
                var page = matching.Take(limit).ToList();
                return new ConversationRange
                {
                    Events = page,
                    NextCursor = page.Count > 0 ? page[^1].Seq : null,
                    HasMore = matching.Count > limit,
                };
            });
    }

    private static TurnEvent Evt(long seq) => new()
    {
        Seq = seq,
        Event = new DeltaEvent { Text = $"chunk-{seq}" },
    };

    /// <summary>The runner's protocol: persist first, then publish.</summary>
    private void Produce(long seq)
    {
        _log.Add(Evt(seq));
        _bus.Publish(_topic, Evt(seq));
    }

    private ConversationStreamer NewStreamer() => new(_bus, _reader, _user);

    [Fact]
    public async Task Replays_the_log_then_streams_live_events()
    {
        Produce(1);
        Produce(2);
        Produce(3); // all in the log before anyone subscribes (bus no-ops)

        var streamer = NewStreamer();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var received = new List<long>();
        await foreach (var evt in streamer.StreamAsync(Pid, Conv, afterSeq: 0, cts.Token))
        {
            received.Add(evt.Seq);

            if (evt.Seq == 3)
            {
                Produce(4); // arrives live, after replay finished
            }

            if (evt.Seq == 4)
            {
                break;
            }
        }

        received.Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public async Task Event_published_during_replay_is_delivered_exactly_once()
    {
        Produce(1);
        Produce(2);

        // Simulate the runner racing the replay read: the moment the streamer
        // reads the DB page (it has already subscribed), event 3 is persisted AND
        // published — so it lands in the live buffer AND in any later DB read.
        var raced = false;
        _reader.When(r => r.ReadRangeAsync(Pid, Conv, 0, Arg.Any<int>(), Arg.Any<CancellationToken>()))
            .Do(_ =>
            {
                if (!raced)
                {
                    raced = true;
                    Produce(3);
                }
            });

        var streamer = NewStreamer();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var received = new List<long>();
        await foreach (var evt in streamer.StreamAsync(Pid, Conv, afterSeq: 0, cts.Token))
        {
            received.Add(evt.Seq);
            if (evt.Seq == 3)
            {
                break;
            }
        }

        // Event 3 was in both the replay page and the live buffer; the seq
        // watermark must deliver it exactly once, in order.
        received.Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task Resumes_from_cursor_skipping_already_seen_events()
    {
        Produce(1);
        Produce(2);
        Produce(3);

        var streamer = NewStreamer();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var received = new List<long>();
        await foreach (var evt in streamer.StreamAsync(Pid, Conv, afterSeq: 2, cts.Token))
        {
            received.Add(evt.Seq);
            if (evt.Seq == 4)
            {
                break;
            }

            Produce(4);
        }

        received.Should().Equal(3, 4);
    }

    [Fact]
    public async Task Overflow_drop_re_splices_from_the_watermark_without_loss()
    {
        Produce(1);

        var streamer = NewStreamer();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var received = new List<long>();
        await foreach (var evt in streamer.StreamAsync(Pid, Conv, afterSeq: 0, cts.Token))
        {
            received.Add(evt.Seq);

            if (evt.Seq == 1)
            {
                // The subscription is live (subscribe precedes replay). Flood it
                // far past the bounded buffer without letting the consumer drain:
                // the bus drops the subscription mid-flood, and the streamer must
                // recover the tail from the log.
                for (long seq = 2; seq <= 300; seq++)
                {
                    Produce(seq);
                }
            }

            if (evt.Seq == 300)
            {
                break;
            }
        }

        received.Should().Equal(Enumerable.Range(1, 300).Select(i => (long)i));
    }
}
