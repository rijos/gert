using FluentAssertions;
using Gert.Model.Dtos;
using Gert.Model.Events;
using Gert.Service.Chat;
using Gert.Service.Tools;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gert.Service.Tests;

/// <summary>
/// The ask_user tool (chat-and-tools.md section Ask the user): argument validation as
/// model-correctable errors, the emit ordering (question_asked before the wait,
/// question_answered before the result), the graceful timeout result, the
/// deadline budget math, and key release on every exit path.
/// </summary>
public sealed class AskUserToolTests
{
    private const string Pid = "default";
    private const string Conv = "conv-1";
    private const string CallId = "call_ask_user_1";

    private static readonly TurnKey Key = new("https://idp.example", "sub-123", Pid, Conv);

    private readonly TurnQuestions _registry = new();
    private readonly TestUserContext _user = new();
    private readonly List<ChatEvent> _emitted = [];

    private AskUserTool NewTool(TurnOptions? options = null, TimeProvider? clock = null) =>
        new(_registry, _user, clock ?? TimeProvider.System, Options.Create(options ?? new TurnOptions()));

    private ToolInvocation Invocation(
        string args,
        bool withEmit = true,
        string? conversationId = Conv,
        DateTimeOffset? deadline = null) => new()
    {
        Pid = Pid,
        ArgumentsJson = args,
        ConversationId = conversationId,
        MessageId = "assistant-msg-1",
        ToolCallId = CallId,
        EmitAsync = withEmit
            ? (chatEvent, _) =>
            {
                lock (_emitted)
                {
                    _emitted.Add(chatEvent);
                }

                return Task.CompletedTask;
            }
            : null,
        Deadline = deadline,
    };

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

    [Theory]
    [InlineData("not json", "invalid arguments")]
    [InlineData("[]", "not a JSON object")]
    [InlineData("{}", "question is required")]
    [InlineData("""{"question":"   "}""", "question is required")]
    [InlineData("""{"question":"q","options":"red"}""", "options must be an array")]
    [InlineData("""{"question":"q","options":["red",""]}""", "options must be non-empty strings")]
    [InlineData("""{"question":"q","allow_free_text":false}""", "allow_free_text=false requires options")]
    public async Task Bad_arguments_are_tool_errors_the_model_can_correct(string args, string expected)
    {
        var result = await NewTool().ExecuteAsync(Invocation(args));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain(expected);
        AskedEvent().Should().BeNull("nothing must reach the user for invalid arguments");
    }

    [Fact]
    public async Task Too_many_or_too_long_options_are_refused()
    {
        var nine = string.Join(",", Enumerable.Range(1, 9).Select(i => $"\"opt{i}\""));
        var many = await NewTool().ExecuteAsync(Invocation($$"""{"question":"q","options":[{{nine}}]}"""));
        many.Success.Should().BeFalse();
        many.Error.Should().Contain("too many options");

        var longOption = new string('x', AskUserTool.MaxOptionChars + 1);
        var tooLong = await NewTool().ExecuteAsync(
            Invocation($$"""{"question":"q","options":["{{longOption}}"]}"""));
        tooLong.Success.Should().BeFalse();
        tooLong.Error.Should().Contain("option is too long");
    }

    [Fact]
    public async Task A_missing_conversation_is_a_tool_error()
    {
        var result = await NewTool().ExecuteAsync(
            Invocation("""{"question":"q"}""", conversationId: null));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("conversation context");
    }

    [Fact]
    public async Task A_host_without_the_emit_seam_is_a_tool_error_not_an_invisible_wait()
    {
        var result = await NewTool().ExecuteAsync(
            Invocation("""{"question":"q"}""", withEmit: false));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("streaming host");
    }

    [Fact]
    public async Task An_answer_resolves_the_call_with_both_events_in_order()
    {
        var task = NewTool().ExecuteAsync(
            Invocation("""{"question":"Which color?","options":["red","blue"]}"""));
        var asked = await WaitForQuestionAsync();

        // The question event folds onto the call's card and carries the full payload.
        asked.Id.Should().Be(CallId);
        asked.Question.Should().Be("Which color?");
        asked.Options.Should().Equal("red", "blue");
        asked.AllowFreeText.Should().BeFalse("options without allow_free_text default to closed");

        _registry.Answer(Key, new AnswerRequest { QuestionId = asked.QuestionId, Answer = "blue" })
            .Should().Be(AnswerOutcome.Delivered);

        var result = await task;
        result.Success.Should().BeTrue();
        result.ResultJson.Should().Contain("\"answered\":true").And.Contain("blue");
        result.Stdout.Should().Be("blue");

        var answered = _emitted.OfType<QuestionAnsweredEvent>().Single();
        answered.Id.Should().Be(CallId);
        answered.QuestionId.Should().Be(asked.QuestionId);
        answered.Answer.Should().Be("blue");

        // The key is released - the conversation's next question can open.
        using var next = _registry.Open(Key, new QuestionPayload("again?", [], true));
    }

    [Fact]
    public async Task A_question_without_options_defaults_to_free_text()
    {
        var options = new TurnOptions { AskUserTimeout = TimeSpan.Zero };
        await NewTool(options).ExecuteAsync(Invocation("""{"question":"Anything?"}"""));

        AskedEvent()!.AllowFreeText.Should().BeTrue();
    }

    [Fact]
    public async Task A_timeout_is_a_graceful_no_response_result_not_an_error()
    {
        var options = new TurnOptions { AskUserTimeout = TimeSpan.Zero };

        var result = await NewTool(options).ExecuteAsync(Invocation("""{"question":"q"}"""));

        result.Success.Should().BeTrue("a silent user is an outcome the model continues from");
        result.ResultJson.Should().Contain("\"answered\":false").And.Contain("timeout");
        result.Stdout.Should().Be("The user did not respond.");
        _emitted.OfType<QuestionAnsweredEvent>().Should().BeEmpty();

        using var next = _registry.Open(Key, new QuestionPayload("again?", [], true));
    }

    [Fact]
    public async Task The_wait_is_capped_by_the_turn_deadline_minus_the_grace()
    {
        // 5-minute knob, but the turn dies in 10s and the grace is 15s: the
        // effective wait floors at zero -> the graceful timeout result lands
        // immediately, well before the turn-budget error finalize would.
        var clock = new FixedClock(DateTimeOffset.Parse("2026-06-10T12:00:00Z"));
        var tool = NewTool(new TurnOptions { AskUserTimeout = TimeSpan.FromMinutes(5) }, clock);

        var result = await tool.ExecuteAsync(Invocation(
            """{"question":"q"}""",
            deadline: clock.GetUtcNow() + TimeSpan.FromSeconds(10)));

        result.Success.Should().BeTrue();
        result.ResultJson.Should().Contain("\"answered\":false");
    }

    [Fact]
    public async Task A_turn_cancel_unwinds_the_wait_and_releases_the_key()
    {
        using var cts = new CancellationTokenSource();
        var task = NewTool().ExecuteAsync(Invocation("""{"question":"q"}"""), cts.Token);
        await WaitForQuestionAsync();

        cts.Cancel();

        await task.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
        using var next = _registry.Open(Key, new QuestionPayload("again?", [], true));
    }

    [Fact]
    public async Task A_second_question_while_one_pends_is_a_tool_error()
    {
        using var occupied = _registry.Open(Key, new QuestionPayload("first?", [], true));

        var result = await NewTool().ExecuteAsync(Invocation("""{"question":"second?"}"""));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("already pending");
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
