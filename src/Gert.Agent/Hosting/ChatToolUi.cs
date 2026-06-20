using Gert.Agent;
using Gert.Model.Events;
using Gert.Tools;
using Gert.Tools.Ui;

namespace Gert.Agent.Hosting;

/// <summary>
/// The chat loop's <see cref="IToolUi"/> (chat-and-tools.md section Ask the user) - the human
/// interaction machinery <c>AskUserTool</c> used to own, moved behind the port. Constructed once per
/// turn (the host is per-turn now) with everything an interaction needs: the question registry, the
/// runner's persist-then-publish emit, the turn key, the clock, and the wait budget. The tool-call
/// id rides each request (<see cref="InteractionRequest.CorrelationId"/>), not the ctor. Opens a
/// pending question, emits <c>question_asked</c> before the wait, and on an answer emits
/// <c>question_answered</c> before resolving - the wire protocol is unchanged.
/// </summary>
internal sealed class ChatToolUi : IToolUi
{
    /// <summary>
    /// Slice reserved off the turn deadline so the timeout result (emit + row +
    /// final round) lands before the runner's lifetime token fires.
    /// </summary>
    public static readonly TimeSpan DeadlineGrace = TimeSpan.FromSeconds(15);

    private readonly ITurnQuestions _questions;
    private readonly Func<ChatEvent, CancellationToken, Task> _emit;
    private readonly TurnKey _key;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _askUserTimeout;
    private readonly DateTimeOffset? _deadline;

    public ChatToolUi(
        ITurnQuestions questions,
        Func<ChatEvent, CancellationToken, Task> emit,
        TurnKey key,
        TimeProvider clock,
        TimeSpan askUserTimeout,
        DateTimeOffset? deadline)
    {
        _questions = questions ?? throw new ArgumentNullException(nameof(questions));
        _emit = emit ?? throw new ArgumentNullException(nameof(emit));
        _key = key;
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

        var payload = new QuestionPayload(request.Prompts
            .Select(p => new QuestionItem(p.Text, p.Header, p.Options, p.AllowFreeText))
            .ToList());

        IPendingQuestion pending;
        try
        {
            pending = _questions.Open(_key, payload);
        }
        catch (QuestionAlreadyPendingException ex)
        {
            // One question per turn - a model-correctable error, not a torn turn.
            return new InteractionResult { Answered = false, Error = ex.Message };
        }

        // The using guarantees the registry never leaks the key - answer,
        // timeout, cancel, or a fault below all release it.
        using (pending)
        {
            await _emit(
                new QuestionAskedEvent
                {
                    Id = request.CorrelationId,
                    QuestionId = pending.QuestionId,
                    Questions = request.Prompts
                        .Select(p => new AskedQuestion(p.Text, p.Header, p.Options, p.AllowFreeText))
                        .ToList(),
                },
                cancellationToken).ConfigureAwait(false);

            // Effective wait: the knob, capped by what remains of the turn budget
            // minus the grace slice (floor zero -> immediate timeout).
            var wait = _askUserTimeout;
            if (_deadline is { } deadline)
            {
                var remaining = deadline - _clock.GetUtcNow() - DeadlineGrace;
                if (remaining < wait)
                {
                    wait = remaining;
                }
            }

            // A user cancel (or shutdown/turn budget) cancels the wait -> OCE ->
            // the runner's cancel/error finalize; the using above releases the key.
            var answers = await pending.WaitAsync(wait, cancellationToken).ConfigureAwait(false);

            if (answers is null)
            {
                // Timeout: the graceful "no response" path - no question_answered event.
                return new InteractionResult { Answered = false };
            }

            await _emit(
                new QuestionAnsweredEvent
                {
                    Id = request.CorrelationId,
                    QuestionId = pending.QuestionId,
                    Answers = answers,
                },
                cancellationToken).ConfigureAwait(false);

            return new InteractionResult { Answered = true, Answers = answers };
        }
    }
}
