using Gert.Model.Events;
using Gert.Tools.Ui;
using Gert.TurnControl;

namespace Gert.Agent.Hosting;

/// <summary>
/// The chat loop's <see cref="IToolUi"/> (chat-and-tools.md section Ask the user) - the human
/// interaction machinery <c>AskUserTool</c> used to own, moved behind the port. Constructed once per
/// turn (the host is per-turn now) with everything an interaction needs: the turn's
/// <see cref="ITurnControlSubscription"/>, the runner's persist-then-publish emit, the clock, and the
/// wait budget. The tool-call id rides each request (<see cref="InteractionRequest.CorrelationId"/>),
/// not the ctor. Opens a question on the bus (so the answer endpoint can validate + route to it), emits
/// <c>question_asked</c> before the wait, AWAITS the answer (a real completion, not a poll - the bus
/// delivers it within the runner's process), and on an answer emits <c>question_answered</c> before
/// resolving. A wait-budget expiry is the graceful "no response"; a turn cancel/shutdown unwinds with
/// <see cref="OperationCanceledException"/>. The wire protocol is unchanged.
/// </summary>
internal sealed class ChatToolUi : IToolUi
{
    /// <summary>
    /// Slice reserved off the turn deadline so the timeout result (emit + final round) lands
    /// before the runner's lifetime token fires.
    /// </summary>
    public static readonly TimeSpan DeadlineGrace = TimeSpan.FromSeconds(15);

    private readonly ITurnControlSubscription _control;
    private readonly Func<ChatEvent, CancellationToken, Task> _emit;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _askUserTimeout;
    private readonly DateTimeOffset? _deadline;

    public ChatToolUi(
        ITurnControlSubscription control,
        Func<ChatEvent, CancellationToken, Task> emit,
        TimeProvider clock,
        TimeSpan askUserTimeout,
        DateTimeOffset? deadline)
    {
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _emit = emit ?? throw new ArgumentNullException(nameof(emit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _askUserTimeout = askUserTimeout;
        _deadline = deadline;
    }

    /// <inheritdoc />
    public async Task<InteractionResult> AskAsync(
        InteractionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Server-minted question id (a GUID), NOT the model's tool-call id (which correlates the
        // SPA card). One question per turn is preserved by the loop running tool calls sequentially.
        var questionId = Guid.NewGuid().ToString("D");
        var asked = request.Prompts
            .Select(p => new AskedQuestion(p.Text, p.Header, p.Options, p.AllowFreeText))
            .ToList();

        await _control.OpenQuestionAsync(questionId, asked, cancellationToken).ConfigureAwait(false);
        try
        {
            await _emit(
                new QuestionAskedEvent
                {
                    Id = request.CorrelationId,
                    QuestionId = questionId,
                    Questions = asked,
                },
                cancellationToken).ConfigureAwait(false);

            // Effective wait: the knob, capped by what remains of the turn budget minus the grace slice
            // (floor zero -> immediate timeout), so the graceful path always beats the lifetime token.
            var wait = _askUserTimeout;
            if (_deadline is { } deadline)
            {
                var remaining = deadline - _clock.GetUtcNow() - DeadlineGrace;
                if (remaining < wait)
                {
                    wait = remaining;
                }
            }

            var answers = await WaitForAnswerAsync(questionId, wait, cancellationToken).ConfigureAwait(false);
            if (answers is null)
            {
                // Wait budget expired: the graceful "no response" path - no question_answered event.
                return new InteractionResult { Answered = false };
            }

            await _emit(
                new QuestionAnsweredEvent
                {
                    Id = request.CorrelationId,
                    QuestionId = questionId,
                    Answers = answers,
                },
                cancellationToken).ConfigureAwait(false);

            return new InteractionResult { Answered = true, Answers = answers };
        }
        finally
        {
            // Seal the question so a late answer 404s, even when the wait ended by cancel. Best-effort
            // on CancellationToken.None so it still runs while the turn token is the reason we unwound.
            await _control.CloseQuestionAsync(questionId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Await the answer, distinguishing a wait-budget expiry (return null -> graceful "no response")
    /// from a turn cancel/shutdown (rethrow the <see cref="OperationCanceledException"/> for the
    /// runner's cancel finalize). The budget runs on the injected clock so a fake clock drives it in tests.
    /// </summary>
    private async Task<IReadOnlyList<string>?> WaitForAnswerAsync(
        string questionId,
        TimeSpan wait,
        CancellationToken cancellationToken)
    {
        using var budget = wait > TimeSpan.Zero
            ? new CancellationTokenSource(wait, _clock)
            : new CancellationTokenSource();
        if (wait <= TimeSpan.Zero)
        {
            budget.Cancel();
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        try
        {
            return await _control.WaitForAnswerAsync(questionId, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return null; // the wait budget expired, not the turn - a graceful timeout, not a torn turn
        }
    }
}
