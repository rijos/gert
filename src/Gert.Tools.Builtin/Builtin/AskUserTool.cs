using Gert.Tools;
using Gert.Tools.Args;
using Gert.Tools.Hosting;
using Gert.Tools.Results;
using Gert.Tools.Ui;
using Gert.Validation;

namespace Gert.Tools.Builtin;

/// <summary>
/// The ask-user tool (chat-and-tools.md section Ask the user). Model function
/// <c>ask_user</c>: show up to FOUR questions mid-turn (rendered as tabs) and
/// block until they are all answered, the wait times out, or the turn is
/// cancelled. The typed-args base (<see cref="ToolCallModal{TArgs, TResult}"/>) parses and
/// validates the <see cref="AskUserArgs"/> (the caps + the model-correctable errors live in
/// <c>AskUserArgsValidator</c>); the tool owns only the answered/timeout result shape. The
/// human-interaction machinery (the control channel, the wire events, the deadline budget)
/// lives behind <see cref="IToolHost.Ui"/> (the chat loop's <c>ChatToolUi</c>), so the tool
/// depends on the <see cref="IToolUi"/> contract, not the chat impl.
/// <para>
/// <see cref="ToolType.Modal"/> (via <see cref="ToolCallModal{TArgs, TResult}"/>) exempts the wait
/// from the per-tool <c>ToolBounds.CallTimeout</c>. A timeout is a SUCCESSFUL result the model
/// continues from; a host with no Ui (autonomous driver) fails the call closed.
/// <see cref="RequiresHuman"/> keeps the tool off an autonomous driver's advertised set.
/// </para>
/// </summary>
public sealed class AskUserTool : ToolCallModal<AskUserArgs, AskUserResult>
{
    /// <param name="validation">The fail-closed provider the base uses to prove args.</param>
    public AskUserTool(IValidationProvider validation)
        : base(validation)
    {
    }

    /// <inheritdoc />
    public override string Id => "ask_user";

    /// <inheritdoc />
    public override string Name => "ask_user";

    /// <inheritdoc />
    public override string Title => "Ask me";

    /// <inheritdoc />
    public override string Icon => "user";

    /// <inheritdoc />
    public override string Group => "standard";

    /// <inheritdoc />
    public override bool RequiresHuman => true;

    /// <inheritdoc />
    public override string Description =>
        "Ask the user up to four clarifying questions at once and wait for their "
        + "answers (shown as tabs). If the result says they did not "
        + "respond, continue with your best judgement.";

    /// <inheritdoc />
    public override async Task<ToolCallResult<AskUserResult>> CallAsync(
        AskUserArgs args,
        ToolInvocation invocation,
        IToolHost host,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(host);

        // No human-interaction surface (autonomous driver): fail closed and
        // readable. RequiresHuman also keeps the tool off such a driver's
        // advertised set; this is the execution-time backstop.
        if (host.Ui is null)
        {
            return ToolCallResult<AskUserResult>.Fail("ask_user is not available in this context");
        }

        // The question events fold onto this call's card: without a call id the Ui
        // could never correlate them, so fail closed rather than wait invisibly.
        if (string.IsNullOrEmpty(invocation.ToolCallId))
        {
            return ToolCallResult<AskUserResult>.Fail("ask_user requires a tool-call id");
        }

        var prompts = args.Questions.Select(ToPrompt).ToList();
        var result = await host.Ui.AskAsync(new InteractionRequest(invocation.ToolCallId, prompts), cancellationToken)
            .ConfigureAwait(false);

        if (result.Error is not null)
        {
            // The Ui rejected the request (e.g. a question already pends) - a
            // model-correctable tool error, not a torn turn.
            return ToolCallResult<AskUserResult>.Fail(result.Error);
        }

        if (!result.Answered)
        {
            // Timeout is an ordinary tool_result, no extra event; the model is
            // told to continue with its best judgement.
            return ToolCallResult<AskUserResult>.Ok(
                new AskUserResult { Answered = false, Reason = "timeout" },
                stdout: "The user did not respond.");
        }

        // Pair each prompt with its answer so the model has context for which
        // prompt each reply belongs to.
        var answers = prompts
            .Select((p, i) => new AskUserAnswer { Question = p.Text, Answer = result.Answers[i] })
            .ToList();

        return ToolCallResult<AskUserResult>.Ok(
            new AskUserResult { Answered = true, Answers = answers },
            stdout: string.Join("\n", answers.Select(a => $"{a.Question} {a.Answer}")));
    }

    // The validated question -> the transport-neutral prompt: trim the text, drop a blank
    // header, and apply the schema default (free text is the norm for an open question, off
    // when a closed option set was offered).
    private static InteractionPrompt ToPrompt(AskUserQuestion question)
    {
        var header = string.IsNullOrWhiteSpace(question.Header) ? null : question.Header.Trim();
        var options = question.Options ?? [];
        var allowFreeText = question.AllowFreeText ?? options.Count == 0;
        return new InteractionPrompt(question.Question.Trim(), header, options, allowFreeText);
    }
}
