using FluentAssertions;
using Gert.Agent.Hosting;
using Gert.Model.Events;
using Gert.Tools.Ui;
using Gert.TurnControl;
using Xunit;

namespace Gert.Agent.Tests;

/// <summary>
/// The chat loop's <see cref="IToolUi"/> (chat-and-tools.md section Ask the user) - now an AWAIT over
/// the turn's <see cref="ITurnControlSubscription"/>, the in-process control plane that replaced the
/// poll-the-db back-channel. The contract the tests pin: question_asked emitted before the wait, an
/// answer resolving AskAsync and emitting question_answered in order, the graceful timeout (no
/// question_answered), the deadline-minus-grace wait cap, the question sealed on the way out, and
/// cancellation unwinding with an OperationCanceledException. The fake subscription stands in for the
/// bus: the test delivers an answer by completing the open question's waiter.
/// </summary>
public sealed class ChatToolUiTests
{
    private const string CallId = "call_ask_user_1";

    private readonly FakeSubscription _control = new();
    private readonly List<ChatEvent> _emitted = [];

    private static InteractionRequest OneOpen =>
        new(CallId, [new InteractionPrompt("again?", null, [], AllowFreeText: true)]);

    private Task EmitAsync(ChatEvent ev, CancellationToken token)
    {
        lock (_emitted)
        {
            _emitted.Add(ev);
        }

        return Task.CompletedTask;
    }

    private ChatToolUi NewUi(
        TimeSpan? askUserTimeout = null,
        TimeProvider? clock = null,
        DateTimeOffset? deadline = null) =>
        new(
            _control,
            EmitAsync,
            clock ?? TimeProvider.System,
            askUserTimeout ?? TimeSpan.FromMinutes(5),
            deadline);

    private QuestionAskedEvent? AskedEvent()
    {
        lock (_emitted)
        {
            return _emitted.OfType<QuestionAskedEvent>().FirstOrDefault();
        }
    }

    private async Task<QuestionAskedEvent> WaitForQuestionAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (AskedEvent() is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }

        return AskedEvent() ?? throw new InvalidOperationException("question_asked never emitted");
    }

    [Fact]
    public async Task An_answer_resolves_with_both_events_in_order()
    {
        var task = NewUi().AskAsync(new InteractionRequest(
            CallId,
            [new InteractionPrompt("Which color?", null, ["red", "blue"], AllowFreeText: false)]));
        var asked = await WaitForQuestionAsync();

        // The question event folds onto the call's card and carries the full payload.
        asked.Id.Should().Be(CallId);
        var only = asked.Questions.Should().ContainSingle().Subject;
        only.Question.Should().Be("Which color?");
        only.Options.Should().Equal("red", "blue");
        only.AllowFreeText.Should().BeFalse();

        // The bus learned the asked shape (so it can validate an answer) under the minted id.
        _control.OpenQuestionId.Should().Be(asked.QuestionId);

        // question_answered must NOT precede the answer.
        _emitted.OfType<QuestionAnsweredEvent>().Should().BeEmpty();

        _control.DeliverAnswer("blue");

        var result = await task;
        result.Answered.Should().BeTrue();
        result.Answers.Should().Equal("blue");

        var answered = _emitted.OfType<QuestionAnsweredEvent>().Single();
        answered.Id.Should().Be(CallId);
        answered.QuestionId.Should().Be(asked.QuestionId);
        answered.Answers.Should().Equal("blue");

        // question_asked precedes question_answered.
        _emitted.IndexOf(asked).Should().BeLessThan(_emitted.IndexOf(answered));

        // The question was sealed on the way out (a late answer would 404).
        _control.Closed.Should().BeTrue();
    }

    [Fact]
    public async Task Several_questions_pair_answers_in_order()
    {
        var task = NewUi().AskAsync(new InteractionRequest(
            CallId,
            [
                new InteractionPrompt("Which color?", "Color", ["red", "blue"], AllowFreeText: false),
                new InteractionPrompt("Anything else?", "Notes", [], AllowFreeText: true),
                new InteractionPrompt("Ship it?", null, ["yes", "no"], AllowFreeText: true),
            ]));
        var asked = await WaitForQuestionAsync();

        asked.Questions.Select(q => q.Question)
            .Should().Equal("Which color?", "Anything else?", "Ship it?");
        asked.Questions[0].Header.Should().Be("Color");

        _control.DeliverAnswer("blue", "looks good", "later");

        var result = await task;
        result.Answered.Should().BeTrue();
        result.Answers.Should().Equal("blue", "looks good", "later");
        _emitted.OfType<QuestionAnsweredEvent>().Single().Answers
            .Should().Equal("blue", "looks good", "later");
    }

    [Fact]
    public async Task A_zero_timeout_is_a_graceful_no_response_with_no_answered_event()
    {
        var result = await NewUi(askUserTimeout: TimeSpan.Zero).AskAsync(OneOpen);

        result.Answered.Should().BeFalse();
        result.Answers.Should().BeEmpty();
        result.Error.Should().BeNull();
        _emitted.OfType<QuestionAnsweredEvent>().Should().BeEmpty();

        // The question is sealed even on timeout.
        _control.Closed.Should().BeTrue();
    }

    [Fact]
    public async Task The_wait_is_capped_by_the_turn_deadline_minus_the_grace()
    {
        // 5-minute knob, but the turn dies in 10s and the grace is 15s: the
        // effective wait floors at zero -> the graceful timeout result lands
        // immediately, well before the turn-budget error finalize would.
        var clock = new FixedClock(DateTimeOffset.Parse("2026-06-10T12:00:00Z"));
        var ui = NewUi(
            askUserTimeout: TimeSpan.FromMinutes(5),
            clock: clock,
            deadline: clock.GetUtcNow() + TimeSpan.FromSeconds(10));

        var result = await ui.AskAsync(OneOpen);

        result.Answered.Should().BeFalse();
        result.Error.Should().BeNull();
        _emitted.OfType<QuestionAnsweredEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task A_turn_cancel_unwinds_the_wait_and_seals_the_question()
    {
        using var cts = new CancellationTokenSource();
        var task = NewUi().AskAsync(OneOpen, cts.Token);
        await WaitForQuestionAsync();

        cts.Cancel();

        await task.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();

        // The cancel path still seals the question (the finally runs on CancellationToken.None).
        _control.Closed.Should().BeTrue();
        _emitted.OfType<QuestionAnsweredEvent>().Should().BeEmpty();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>
    /// A fake <see cref="ITurnControlSubscription"/> backing only what the Ui touches: it records the
    /// open question (id + shape) and hands the waiter a completion the test resolves with
    /// <see cref="DeliverAnswer"/>, mirroring the bus delivering an accepted answer.
    /// </summary>
    private sealed class FakeSubscription : ITurnControlSubscription
    {
        private readonly CancellationTokenSource _cancel = new();
        private readonly Lock _gate = new();
        private TaskCompletionSource<IReadOnlyList<string>>? _slot;

        public string? OpenQuestionId { get; private set; }

        public bool Closed { get; private set; }

        public CancellationToken Cancelled => _cancel.Token;

        public void DeliverAnswer(params string[] answers)
        {
            lock (_gate)
            {
                _slot!.TrySetResult(answers);
            }
        }

        public Task OpenQuestionAsync(
            string questionId,
            IReadOnlyList<AskedQuestion> questions,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                OpenQuestionId = questionId;
                _slot = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> WaitForAnswerAsync(
            string questionId,
            CancellationToken cancellationToken = default)
        {
            TaskCompletionSource<IReadOnlyList<string>> slot;
            lock (_gate)
            {
                slot = _slot!;
            }

            return slot.Task.WaitAsync(cancellationToken);
        }

        public Task CloseQuestionAsync(string questionId, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                Closed = true;
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _cancel.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
