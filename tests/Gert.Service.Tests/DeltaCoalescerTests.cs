using FluentAssertions;
using Gert.Model.Events;
using Gert.Service.Chat;
using Xunit;

namespace Gert.Service.Tests;

/// <summary>
/// <see cref="DeltaCoalescer"/> in isolation: the coalescing thresholds, reasoning-before-content
/// boundary ordering, and the cancelled-token tail skip. The accumulator half is
/// <see cref="DeltaAccumulatorTests"/>; end-to-end streaming order rides the agent/tee tests.
/// </summary>
public sealed class DeltaCoalescerTests
{
    private static readonly TimeSpan LongWindow = TimeSpan.FromHours(1);

    private static DeltaCoalescer NewCoalescer(List<ChatEvent> emitted, TimeSpan interval, int maxChars = int.MaxValue) =>
        new(
            (ev, _) =>
            {
                emitted.Add(ev);
                return Task.CompletedTask;
            },
            interval,
            maxChars,
            TimeProvider.System);

    [Fact]
    public async Task A_zero_interval_flushes_each_chunk_immediately()
    {
        var emitted = new List<ChatEvent>();
        var coalescer = NewCoalescer(emitted, TimeSpan.Zero);

        await coalescer.AppendText("a", default);
        await coalescer.AppendText("b", default);

        emitted.OfType<DeltaEvent>().Select(e => e.Text).Should().Equal("a", "b");
    }

    [Fact]
    public async Task A_long_window_buffers_until_the_boundary()
    {
        var emitted = new List<ChatEvent>();
        var coalescer = NewCoalescer(emitted, LongWindow);

        await coalescer.AppendText("a", default);
        await coalescer.AppendText("b", default);
        emitted.Should().BeEmpty();

        await coalescer.FlushBoundary(default);
        emitted.OfType<DeltaEvent>().Single().Text.Should().Be("ab");
    }

    [Fact]
    public async Task The_size_backstop_flushes_mid_window()
    {
        var emitted = new List<ChatEvent>();
        var coalescer = NewCoalescer(emitted, LongWindow, maxChars: 3);

        await coalescer.AppendText("ab", default); // 2 < 3, buffered
        emitted.Should().BeEmpty();
        await coalescer.AppendText("cd", default); // 4 >= 3, flushes

        emitted.OfType<DeltaEvent>().Single().Text.Should().Be("abcd");
    }

    [Fact]
    public async Task Reasoning_flushes_before_content_at_a_boundary()
    {
        var emitted = new List<ChatEvent>();
        var coalescer = NewCoalescer(emitted, LongWindow);

        await coalescer.AppendReasoning("think", default);
        await coalescer.AppendText("answer", default);
        await coalescer.FlushBoundary(default);

        emitted.Select(e => e.GetType().Name)
            .Should().Equal(nameof(ReasoningEvent), nameof(DeltaEvent));
    }

    [Fact]
    public async Task Pending_deltas_flush_when_the_interval_elapses()
    {
        var clock = new ManualClock();
        var emitted = new List<ChatEvent>();
        var coalescer = new DeltaCoalescer(
            (ev, _) =>
            {
                emitted.Add(ev);
                return Task.CompletedTask;
            },
            TimeSpan.FromMilliseconds(150),
            int.MaxValue,
            clock);

        // Each arrival past the window flushes what was buffered BEFORE it plus itself.
        await coalescer.AppendText("a ", default);   // t=0, buffered
        clock.Advance(TimeSpan.FromMilliseconds(200));
        await coalescer.AppendText("b ", default);   // t=200 >= 150 -> flush "a b "
        clock.Advance(TimeSpan.FromMilliseconds(200));
        await coalescer.AppendText("c", default);    // t=400, 200 since last flush -> flush "c"

        emitted.OfType<DeltaEvent>().Select(d => d.Text).Should().Equal("a b ", "c");
    }

    [Fact]
    public async Task FlushTails_skips_on_a_cancelled_token_then_emits_on_a_live_one()
    {
        var emitted = new List<ChatEvent>();
        var coalescer = NewCoalescer(emitted, LongWindow);

        await coalescer.AppendText("x", default);

        await coalescer.FlushTails(new CancellationToken(canceled: true));
        emitted.Should().BeEmpty();

        await coalescer.FlushTails(default);
        emitted.OfType<DeltaEvent>().Single().Text.Should().Be("x");
    }

    /// <summary>A manually-advanced clock so the interval-elapsed flush is deterministic.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan by) =>
            _timestamp += (long)(by.TotalSeconds * TimestampFrequency);
    }
}
