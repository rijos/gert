using FluentAssertions;
using Gert.Model.Dtos;
using Gert.Service.Chat;
using Xunit;

namespace Gert.Service.Tests;

/// <summary>
/// The ask_user question registry (rest-api.md § answer a question): one
/// pending question per turn, answer delivery with id/option enforcement,
/// timeout/cancel sealing (a losing answer is NotFound, never silently
/// dropped), tenant-key isolation, and the release semantics shared with
/// <see cref="TurnCancellation"/>.
/// </summary>
public sealed class TurnQuestionsTests
{
    private static readonly TurnKey Key = new("https://idp.example", "sub-123", "default", "conv-1");

    private static readonly QuestionPayload OpenQuestion =
        new("Which color?", [], AllowFreeText: true);

    private static readonly QuestionPayload ClosedQuestion =
        new("Which color?", ["red", "blue"], AllowFreeText: false);

    private readonly TurnQuestions _registry = new();

    private static AnswerRequest Reply(string questionId, string answer) =>
        new() { QuestionId = questionId, Answer = answer };

    [Fact]
    public async Task An_answer_completes_the_wait_with_its_text()
    {
        using var pending = _registry.Open(Key, OpenQuestion);
        var wait = pending.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

        var outcome = _registry.Answer(Key, Reply(pending.QuestionId, "blue"));

        outcome.Should().Be(AnswerOutcome.Delivered);
        (await wait).Should().Be("blue");
    }

    [Fact]
    public async Task The_wait_times_out_to_null_and_seals_the_question()
    {
        using var pending = _registry.Open(Key, OpenQuestion);

        var answer = await pending.WaitAsync(TimeSpan.Zero, CancellationToken.None);

        answer.Should().BeNull();

        // A late answer must NOT report Delivered — the tool already returned
        // "the user did not respond".
        _registry.Answer(Key, Reply(pending.QuestionId, "too late"))
            .Should().Be(AnswerOutcome.NotFound);
    }

    [Fact]
    public async Task Cancellation_throws_and_seals_the_question()
    {
        using var pending = _registry.Open(Key, OpenQuestion);
        using var cts = new CancellationTokenSource();
        var wait = pending.WaitAsync(TimeSpan.FromSeconds(30), cts.Token);

        cts.Cancel();

        await wait.Invoking(w => w).Should().ThrowAsync<OperationCanceledException>();
        _registry.Answer(Key, Reply(pending.QuestionId, "after cancel"))
            .Should().Be(AnswerOutcome.NotFound);
    }

    [Fact]
    public void A_second_question_for_the_same_turn_is_refused()
    {
        using var first = _registry.Open(Key, OpenQuestion);

        var act = () => _registry.Open(Key, OpenQuestion);

        act.Should().Throw<QuestionAlreadyPendingException>();
    }

    [Fact]
    public void A_stale_question_id_is_an_id_mismatch()
    {
        using var pending = _registry.Open(Key, OpenQuestion);

        _registry.Answer(Key, Reply(Guid.NewGuid().ToString("D"), "blue"))
            .Should().Be(AnswerOutcome.IdMismatch);
    }

    [Fact]
    public void An_answer_with_no_pending_question_is_not_found()
    {
        // No tombstones: an answer cannot legitimately precede its question
        // (the client only learns of it from the persisted question_asked).
        _registry.Answer(Key, Reply(Guid.NewGuid().ToString("D"), "blue"))
            .Should().Be(AnswerOutcome.NotFound);
    }

    [Fact]
    public async Task A_closed_question_rejects_an_answer_outside_its_options()
    {
        using var pending = _registry.Open(Key, ClosedQuestion);
        var wait = pending.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

        _registry.Answer(Key, Reply(pending.QuestionId, "green"))
            .Should().Be(AnswerOutcome.InvalidOption);

        // The pending question survives the rejected attempt.
        _registry.Answer(Key, Reply(pending.QuestionId, "red"))
            .Should().Be(AnswerOutcome.Delivered);
        (await wait).Should().Be("red");
    }

    [Fact]
    public async Task Free_text_is_accepted_alongside_options_when_allowed()
    {
        using var pending = _registry.Open(
            Key, new QuestionPayload("Which color?", ["red", "blue"], AllowFreeText: true));
        var wait = pending.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

        _registry.Answer(Key, Reply(pending.QuestionId, "mauve"))
            .Should().Be(AnswerOutcome.Delivered);
        (await wait).Should().Be("mauve");
    }

    [Fact]
    public void Dispose_releases_the_key_so_a_late_answer_is_not_found()
    {
        var pending = _registry.Open(Key, OpenQuestion);
        var questionId = pending.QuestionId;
        pending.Dispose();

        _registry.Answer(Key, Reply(questionId, "blue")).Should().Be(AnswerOutcome.NotFound);

        // The key is free again for the conversation's next turn.
        using var next = _registry.Open(Key, OpenQuestion);
    }

    [Fact]
    public void Disposing_a_predecessor_does_not_release_the_successor_question()
    {
        // The next turn re-registers the key before the previous handle's
        // Dispose runs (the same race TurnCancellation.Release guards).
        var first = _registry.Open(Key, OpenQuestion);
        first.Dispose();
        using var second = _registry.Open(Key, OpenQuestion);
        first.Dispose();

        _registry.Answer(Key, Reply(second.QuestionId, "blue"))
            .Should().Be(AnswerOutcome.Delivered);
    }

    [Fact]
    public void Keys_are_tenant_scoped()
    {
        using var pending = _registry.Open(Key, OpenQuestion);

        // Same conversation id, different tenant: must not address the question.
        _registry.Answer(Key with { Sub = "someone-else" }, Reply(pending.QuestionId, "blue"))
            .Should().Be(AnswerOutcome.NotFound);
    }

    [Fact]
    public async Task A_second_answer_for_the_same_question_is_not_found()
    {
        using var pending = _registry.Open(Key, OpenQuestion);
        var wait = pending.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

        _registry.Answer(Key, Reply(pending.QuestionId, "blue"))
            .Should().Be(AnswerOutcome.Delivered);
        (await wait).Should().Be("blue");

        // Idempotency: the TCS already completed (the key may not be released
        // yet — the tool is still emitting question_answered).
        _registry.Answer(Key, Reply(pending.QuestionId, "red"))
            .Should().Be(AnswerOutcome.NotFound);
    }
}
