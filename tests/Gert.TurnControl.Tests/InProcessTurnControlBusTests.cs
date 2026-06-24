using FluentAssertions;
using Gert.Model.Events;
using Gert.TurnControl;
using Gert.TurnControl.Local;
using Xunit;

namespace Gert.TurnControl.Tests;

/// <summary>
/// The in-process control plane (chat-and-tools.md section detached turns): a publish reaches a live
/// subscription, an answer is validated + delivered, the scope's user key isolates tenants, a queued
/// cancel is caught at subscribe time against the freshness boundary, and a disposed subscription is
/// no longer addressable. These are the contract a networked bus must also satisfy across instances.
/// </summary>
public sealed class InProcessTurnControlBusTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 24, 12, 0, 0, TimeSpan.Zero);

    private static readonly ControlScope ScopeA = new("user-key-a", "default", "11111111-1111-1111-1111-111111111111");

    // Same project + conversation id as ScopeA but a DIFFERENT user key: the tenant-isolation foil.
    private static readonly ControlScope ScopeB = new("user-key-b", "default", "11111111-1111-1111-1111-111111111111");

    private readonly FakeClock _clock = new(T0);

    private static AskedQuestion Closed(params string[] options) =>
        new("Which color?", null, options, AllowFreeText: false);

    private InProcessTurnControlBus NewBus() => new(_clock);

    [Fact]
    public async Task A_cancel_published_to_a_live_turn_trips_its_token()
    {
        var bus = NewBus();
        await using var turn = await bus.SubscribeAsync(ScopeA, T0);

        turn.Cancelled.IsCancellationRequested.Should().BeFalse();
        await bus.PublishCancelAsync(ScopeA);
        turn.Cancelled.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task A_cancel_queued_before_subscribe_is_caught_at_subscribe_time()
    {
        // The user hit stop while the turn was still queued (no subscriber yet): the bus retains it and
        // the runner catches it the moment it subscribes, because the cancel stamp is at/after `since`.
        var bus = NewBus();
        await bus.PublishCancelAsync(ScopeA); // stamped at T0 (no live subscription)

        await using var turn = await bus.SubscribeAsync(ScopeA, since: T0);
        turn.Cancelled.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task A_cancel_older_than_the_turn_is_ignored_as_stale()
    {
        // A leftover cancel from a PRIOR turn of this conversation (stamped before this turn's plan
        // instant) must not cancel the new turn - the freshness boundary.
        var bus = NewBus();
        await bus.PublishCancelAsync(ScopeA); // stamped at T0

        await using var turn = await bus.SubscribeAsync(ScopeA, since: T0 + TimeSpan.FromSeconds(1));
        turn.Cancelled.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public async Task A_cancel_for_no_live_or_queued_turn_is_a_harmless_noop()
    {
        var bus = NewBus();

        // Nobody subscribed and nobody will: the publish is dropped, not an error.
        await bus.Invoking(b => b.PublishCancelAsync(ScopeA)).Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_valid_answer_is_accepted_and_delivered_to_the_waiter()
    {
        var bus = NewBus();
        await using var turn = await bus.SubscribeAsync(ScopeA, T0);
        await turn.OpenQuestionAsync("q-1", [Closed("red", "blue")]);

        var wait = turn.WaitForAnswerAsync("q-1");
        var outcome = await bus.SubmitAnswerAsync(ScopeA, "q-1", ["blue"]);

        outcome.Should().Be(AnswerOutcome.Accepted);
        (await wait.WaitAsync(TimeSpan.FromSeconds(2))).Should().Equal("blue");
    }

    [Fact]
    public async Task An_off_menu_answer_is_invalid_and_leaves_the_question_open()
    {
        var bus = NewBus();
        await using var turn = await bus.SubscribeAsync(ScopeA, T0);
        await turn.OpenQuestionAsync("q-1", [Closed("red", "blue")]);

        (await bus.SubmitAnswerAsync(ScopeA, "q-1", ["green"])).Should().Be(AnswerOutcome.Invalid);

        // Still open: a subsequent offered option is accepted.
        (await bus.SubmitAnswerAsync(ScopeA, "q-1", ["red"])).Should().Be(AnswerOutcome.Accepted);
    }

    [Fact]
    public async Task An_answer_for_an_unknown_question_id_is_NoSuchQuestion()
    {
        var bus = NewBus();
        await using var turn = await bus.SubscribeAsync(ScopeA, T0);
        await turn.OpenQuestionAsync("q-1", [Closed("red", "blue")]);

        (await bus.SubmitAnswerAsync(ScopeA, "q-other", ["blue"])).Should().Be(AnswerOutcome.NoSuchQuestion);
    }

    [Fact]
    public async Task An_answer_with_no_live_turn_is_NoSuchQuestion()
    {
        var bus = NewBus();

        (await bus.SubmitAnswerAsync(ScopeA, "q-1", ["blue"])).Should().Be(AnswerOutcome.NoSuchQuestion);
    }

    [Fact]
    public async Task A_closed_question_is_NoSuchQuestion()
    {
        var bus = NewBus();
        await using var turn = await bus.SubscribeAsync(ScopeA, T0);
        await turn.OpenQuestionAsync("q-1", [Closed("red", "blue")]);
        await turn.CloseQuestionAsync("q-1");

        (await bus.SubmitAnswerAsync(ScopeA, "q-1", ["blue"])).Should().Be(AnswerOutcome.NoSuchQuestion);
    }

    [Fact]
    public async Task A_cancel_only_trips_its_own_scope_not_a_same_conversation_other_tenant()
    {
        // ScopeA and ScopeB share the conversation id but differ by user key: the publish addresses one
        // turn only. This is the control-plane half of tenant isolation.
        var bus = NewBus();
        await using var turnA = await bus.SubscribeAsync(ScopeA, T0);
        await using var turnB = await bus.SubscribeAsync(ScopeB, T0);

        await bus.PublishCancelAsync(ScopeA);

        turnA.Cancelled.IsCancellationRequested.Should().BeTrue();
        turnB.Cancelled.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public async Task An_answer_only_reaches_its_own_scope_not_a_same_conversation_other_tenant()
    {
        var bus = NewBus();
        await using var turnB = await bus.SubscribeAsync(ScopeB, T0);
        await turnB.OpenQuestionAsync("q-1", [Closed("red", "blue")]);

        // ScopeA names a different tenant: even with the right question id, it reaches no open question.
        (await bus.SubmitAnswerAsync(ScopeA, "q-1", ["blue"])).Should().Be(AnswerOutcome.NoSuchQuestion);

        // The owner can still answer it.
        (await bus.SubmitAnswerAsync(ScopeB, "q-1", ["blue"])).Should().Be(AnswerOutcome.Accepted);
    }

    [Fact]
    public async Task A_disposed_subscription_is_no_longer_addressable()
    {
        var bus = NewBus();
        var turn = await bus.SubscribeAsync(ScopeA, T0);
        await turn.OpenQuestionAsync("q-1", [Closed("red", "blue")]);
        await turn.DisposeAsync();

        (await bus.SubmitAnswerAsync(ScopeA, "q-1", ["blue"])).Should().Be(AnswerOutcome.NoSuchQuestion);
        await bus.Invoking(b => b.PublishCancelAsync(ScopeA)).Should().NotThrowAsync();
    }

    [Fact]
    public async Task The_wait_unwinds_when_the_callers_token_cancels()
    {
        var bus = NewBus();
        await using var turn = await bus.SubscribeAsync(ScopeA, T0);
        await turn.OpenQuestionAsync("q-1", [Closed("red", "blue")]);

        using var cts = new CancellationTokenSource();
        var wait = turn.WaitForAnswerAsync("q-1", cts.Token);
        cts.Cancel();

        await wait.Invoking(w => w).Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
