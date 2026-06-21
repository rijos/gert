using System.Runtime.CompilerServices;
using FluentAssertions;
using Gert.Agent.Loop;
using Gert.Chat;
using Gert.Model.Chat;
using Gert.Model.Events;
using Gert.Testing.Fakes;
using Xunit;

namespace Gert.Agent.Tests;

/// <summary>
/// <see cref="DeltaSink"/> in isolation: the coalescing thresholds, reasoning-before-content boundary
/// ordering, the accumulators, and the cancelled-token tail skip. End-to-end streaming ordering rides
/// AgentLoopTests/TurnRunnerTests; these pin the unit's own contract.
/// </summary>
public sealed class DeltaSinkTests
{
    private static readonly TimeSpan LongWindow = TimeSpan.FromHours(1);

    private static DeltaSink NewSink(
        List<ChatEvent> emitted,
        TimeSpan interval,
        int maxChars = int.MaxValue,
        Action<string>? onText = null,
        Action<string>? onReasoning = null)
    {
        var request = new AgentLoopRequest
        {
            Messages = [],
            Tools = new Toolset([], [], new HashSet<string>(StringComparer.Ordinal)),
            ModelId = "m",
            Model = new NullModel(),
            Host = new FakeToolHost(),
            Pid = "p",
            MaxRounds = 1,
            DeltaFlushInterval = interval,
            DeltaFlushMaxChars = maxChars,
            Emit = (ev, _) =>
            {
                emitted.Add(ev);
                return Task.CompletedTask;
            },
            OnText = onText,
            OnReasoning = onReasoning,
        };
        return new DeltaSink(request, TimeProvider.System);
    }

    [Fact]
    public async Task A_zero_interval_flushes_each_chunk_immediately()
    {
        var emitted = new List<ChatEvent>();
        var sink = NewSink(emitted, TimeSpan.Zero);

        await sink.AppendText("a", default);
        await sink.AppendText("b", default);

        emitted.OfType<DeltaEvent>().Select(e => e.Text).Should().Equal("a", "b");
    }

    [Fact]
    public async Task A_long_window_buffers_until_the_boundary()
    {
        var emitted = new List<ChatEvent>();
        var sink = NewSink(emitted, LongWindow);

        await sink.AppendText("a", default);
        await sink.AppendText("b", default);
        emitted.Should().BeEmpty();

        await sink.FlushBoundary(default);
        emitted.OfType<DeltaEvent>().Single().Text.Should().Be("ab");
    }

    [Fact]
    public async Task The_size_backstop_flushes_mid_window()
    {
        var emitted = new List<ChatEvent>();
        var sink = NewSink(emitted, LongWindow, maxChars: 3);

        await sink.AppendText("ab", default); // 2 < 3, buffered
        emitted.Should().BeEmpty();
        await sink.AppendText("cd", default); // 4 >= 3, flushes

        emitted.OfType<DeltaEvent>().Single().Text.Should().Be("abcd");
    }

    [Fact]
    public async Task Reasoning_flushes_before_content_at_a_boundary()
    {
        var emitted = new List<ChatEvent>();
        var sink = NewSink(emitted, LongWindow);

        await sink.AppendReasoning("think", default);
        await sink.AppendText("answer", default);
        await sink.FlushBoundary(default);

        emitted.Select(e => e.GetType().Name)
            .Should().Equal(nameof(ReasoningEvent), nameof(DeltaEvent));
    }

    [Fact]
    public async Task The_accumulators_track_all_text_and_the_round_slice()
    {
        var emitted = new List<ChatEvent>();
        var taps = new List<string>();
        var sink = NewSink(emitted, LongWindow, onText: taps.Add);

        await sink.AppendText("one ", default);
        var mark = sink.Length;
        await sink.AppendReasoning("r", default);
        await sink.AppendText("two", default);

        sink.Content.Should().Be("one two");
        sink.Reasoning.Should().Be("r");
        sink.ContentSince(mark).Should().Be("two");
        taps.Should().Equal("one ", "two");
    }

    [Fact]
    public async Task FlushTails_skips_on_a_cancelled_token_then_emits_on_a_live_one()
    {
        var emitted = new List<ChatEvent>();
        var sink = NewSink(emitted, LongWindow);

        await sink.AppendText("x", default);

        await sink.FlushTails(new CancellationToken(canceled: true));
        emitted.Should().BeEmpty();

        await sink.FlushTails(default);
        emitted.OfType<DeltaEvent>().Single().Text.Should().Be("x");
    }

    private sealed class NullModel : IChatModelClient
    {
        public async IAsyncEnumerable<ChatModelChunk> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
