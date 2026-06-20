using FluentAssertions;
using Gert.Testing.Fakes;
using Gert.Tools;
using Gert.Tools.Builtin;
using Xunit;

namespace Gert.Tools.Builtin.Tests;

/// <summary>
/// The ask_user tool at the tool boundary (chat-and-tools.md section Ask the user): argument
/// validation as model-correctable errors (nothing reaches the Ui), the fail-closed path when the
/// host has no Ui, the InteractionRequest the tool builds from the parsed args, and the
/// answered/timeout/rejection result shapes it derives from the Ui's InteractionResult. The
/// registry/emit/deadline machinery lives behind the port now and is tested in ChatToolUiTests.
/// </summary>
public sealed class AskUserToolTests
{
    private const string Pid = "default";
    private const string Conv = "conv-1";
    private const string CallId = "call_ask_user_1";

    private readonly FakeToolUi _ui = new();
    private readonly FakeToolHost _host;

    public AskUserToolTests() => _host = new FakeToolHost { Ui = _ui };

    private static ToolInvocation Invocation(string args) => new()
    {
        Pid = Pid,
        ArgumentsJson = args,
        ConversationId = Conv,
        MessageId = "assistant-msg-1",
        ToolCallId = CallId,
    };

    [Theory]
    [InlineData("not json", "invalid arguments")]
    [InlineData("[]", "not a JSON object")]
    [InlineData("{}", "questions must be an array")]
    [InlineData("""{"questions":[]}""", "at least one question is required")]
    [InlineData("""{"questions":[{}]}""", "question is required")]
    [InlineData("""{"questions":[{"question":"   "}]}""", "question is required")]
    [InlineData("""{"questions":[{"question":"q","options":"red"}]}""", "options must be an array")]
    [InlineData("""{"questions":[{"question":"q","options":["red",""]}]}""", "options must be non-empty strings")]
    [InlineData("""{"questions":[{"question":"q","allow_free_text":false}]}""", "allow_free_text=false requires options")]
    public async Task Bad_arguments_are_tool_errors_the_model_can_correct(string args, string expected)
    {
        var result = await new AskUserTool().ExecuteAsync(Invocation(args), _host);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain(expected);
        _ui.CapturedRequest.Should().BeNull("nothing must reach the user for invalid arguments");
    }

    [Fact]
    public async Task More_than_four_questions_are_refused()
    {
        var five = string.Join(",", Enumerable.Range(1, 5).Select(i => $$"""{"question":"q{{i}}"}"""));
        var result = await new AskUserTool().ExecuteAsync(Invocation($$"""{"questions":[{{five}}]}"""), _host);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("too many questions");
        _ui.CapturedRequest.Should().BeNull();
    }

    [Fact]
    public async Task Too_many_or_too_long_options_are_refused()
    {
        var nine = string.Join(",", Enumerable.Range(1, 9).Select(i => $"\"opt{i}\""));
        var many = await new AskUserTool().ExecuteAsync(
            Invocation($$"""{"questions":[{"question":"q","options":[{{nine}}]}]}"""), _host);
        many.Success.Should().BeFalse();
        many.Error.Should().Contain("too many options");

        var longOption = new string('x', AskUserTool.MaxOptionChars + 1);
        var tooLong = await new AskUserTool().ExecuteAsync(
            Invocation($$"""{"questions":[{"question":"q","options":["{{longOption}}"]}]}"""), _host);
        tooLong.Success.Should().BeFalse();
        tooLong.Error.Should().Contain("option is too long");

        _ui.CapturedRequest.Should().BeNull();
    }

    [Fact]
    public async Task A_too_long_header_is_refused()
    {
        var longHeader = new string('h', AskUserTool.MaxHeaderChars + 1);
        var result = await new AskUserTool().ExecuteAsync(
            Invocation($$"""{"questions":[{"question":"q","header":"{{longHeader}}"}]}"""), _host);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("header is too long");
        _ui.CapturedRequest.Should().BeNull();
    }

    [Fact]
    public async Task A_host_without_a_ui_fails_closed()
    {
        var host = new FakeToolHost { Ui = null };

        var result = await new AskUserTool().ExecuteAsync(
            Invocation("""{"questions":[{"question":"q"}]}"""), host);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not available");
    }

    [Fact]
    public async Task An_answer_is_paired_with_its_question_in_the_result()
    {
        _ui.Result = new InteractionResult { Answered = true, Answers = ["blue"] };

        var result = await new AskUserTool().ExecuteAsync(
            Invocation("""{"questions":[{"question":"Which color?","options":["red","blue"]}]}"""), _host);

        // The request the tool built carries the parsed prompt.
        var prompt = _ui.CapturedRequest!.Prompts.Should().ContainSingle().Subject;
        prompt.Text.Should().Be("Which color?");
        prompt.Options.Should().Equal("red", "blue");
        prompt.AllowFreeText.Should().BeFalse("options without allow_free_text default to closed");

        result.Success.Should().BeTrue();
        result.ResultJson.Should().Contain("\"answered\":true").And.Contain("blue");
        result.Stdout.Should().Contain("Which color?").And.Contain("blue");
    }

    [Fact]
    public async Task Several_questions_carry_their_prompts_and_pair_with_answers_in_order()
    {
        _ui.Result = new InteractionResult
        {
            Answered = true,
            Answers = ["blue", "looks good", "later"],
        };

        var invocation = Invocation(
            """
            {"questions":[
              {"question":"Which color?","header":"Color","options":["red","blue"]},
              {"question":"Anything else?","header":"Notes"},
              {"question":"Ship it?","options":["yes","no"],"allow_free_text":true}
            ]}
            """);
        var result = await new AskUserTool().ExecuteAsync(invocation, _host);

        var prompts = _ui.CapturedRequest!.Prompts;
        prompts.Select(p => p.Text).Should().Equal("Which color?", "Anything else?", "Ship it?");
        prompts[0].Header.Should().Be("Color");
        prompts[1].Header.Should().Be("Notes");
        prompts[1].AllowFreeText.Should().BeTrue("no options defaults open");
        prompts[2].AllowFreeText.Should().BeTrue("explicit allow_free_text alongside options");

        result.Success.Should().BeTrue();
        // Each question is paired with its answer for the model's context.
        result.ResultJson.Should().Contain("Which color?").And.Contain("blue")
            .And.Contain("Anything else?").And.Contain("looks good");
    }

    [Fact]
    public async Task A_timeout_is_a_graceful_no_response_result_not_an_error()
    {
        _ui.Result = new InteractionResult { Answered = false };

        var result = await new AskUserTool().ExecuteAsync(
            Invocation("""{"questions":[{"question":"q"}]}"""), _host);

        result.Success.Should().BeTrue("a silent user is an outcome the model continues from");
        result.ResultJson.Should().Contain("\"answered\":false").And.Contain("timeout");
        result.Stdout.Should().Be("The user did not respond.");
    }

    [Fact]
    public async Task A_ui_rejection_is_a_tool_error_the_model_can_correct()
    {
        _ui.Result = new InteractionResult
        {
            Answered = false,
            Error = "a question is already pending for this turn",
        };

        var result = await new AskUserTool().ExecuteAsync(
            Invocation("""{"questions":[{"question":"q"}]}"""), _host);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("already pending");
    }
}
