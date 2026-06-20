using FluentAssertions;
using Gert.Model.Dtos;
using Gert.Model.Events;
using Gert.Service.Chat;
using Gert.Testing;
using Gert.Tools;
using Xunit;

namespace Gert.Service.Tests;

/// <summary>
/// The chat loop's <see cref="IToolUi"/> (chat-and-tools.md section Ask the user) - the
/// human-interaction machinery moved out of AskUserTool: question_asked emitted before the wait,
/// an answer resolving AskAsync and emitting question_answered in order (releasing the key), the
/// graceful timeout (no question_answered), the deadline-minus-grace wait cap, cancellation
/// unwinding, and the one-question-per-turn rejection - all against a real TurnQuestions registry.
/// </summary>
public sealed class ChatToolUiTests
{
    private const string Pid = "default";
    private const string Conv = "conv-1";
    private const string CallId = "call_ask_user_1";

    private static readonly TurnKey Key = new("https://idp.example", "sub-123", Pid, Conv);

    private readonly TurnQuestions _registry = new();
    private readonly List<ChatEvent> _emitted = [];

    private static InteractionRequest OneOpen =>
        new(CallId, [new InteractionPrompt("again?", null, [], AllowFreeText: true)]);

    private static QuestionPayload OneOpenPayload =>
        new([new QuestionItem("again?", null, [], AllowFreeText: true)]);

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
            _registry,
            EmitAsync,
            Key,
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
            await Task.Delay(10);
        }

        return AskedEvent() ?? throw new InvalidOperationException("question_asked never emitted");
    }

    [Fact]
    public async Task An_answer_resolves_with_both_events_in_order_and_releases_the_key()
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

        // question_answered must NOT precede the answer.
        _emitted.OfType<QuestionAnsweredEvent>().Should().BeEmpty();

        _registry.Answer(Key, Proof.Of(new AnswerRequest { QuestionId = asked.QuestionId, Answers = ["blue"] }))
            .Should().Be(AnswerOutcome.Delivered);

        var result = await task;
        result.Answered.Should().BeTrue();
        result.Answers.Should().Equal("blue");

        var answered = _emitted.OfType<QuestionAnsweredEvent>().Single();
        answered.Id.Should().Be(CallId);
        answered.QuestionId.Should().Be(asked.QuestionId);
        answered.Answers.Should().Equal("blue");

        // question_asked precedes question_answered.
        _emitted.IndexOf(asked).Should().BeLessThan(_emitted.IndexOf(answered));

        // The key is released - the conversation's next question can open.
        using var next = _registry.Open(Key, OneOpenPayload);
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

        _registry.Answer(Key, Proof.Of(new AnswerRequest
        {
            QuestionId = asked.QuestionId,
            Answers = ["blue", "looks good", "later"],
        })).Should().Be(AnswerOutcome.Delivered);

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

        // The key is released even on timeout.
        using var next = _registry.Open(Key, OneOpenPayload);
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
    public async Task A_turn_cancel_unwinds_the_wait_and_releases_the_key()
    {
        using var cts = new CancellationTokenSource();
        var task = NewUi().AskAsync(OneOpen, cts.Token);
        await WaitForQuestionAsync();

        cts.Cancel();

        await task.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
        using var next = _registry.Open(Key, OneOpenPayload);
    }

    [Fact]
    public async Task A_second_ask_while_one_pends_returns_a_model_correctable_error()
    {
        using var occupied = _registry.Open(Key, OneOpenPayload);

        var result = await NewUi().AskAsync(OneOpen);

        result.Answered.Should().BeFalse();
        result.Error.Should().Contain("already pending");
        // The rejection emits nothing - the first question still owns the card.
        AskedEvent().Should().BeNull();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
