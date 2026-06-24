using System.Collections.Concurrent;
using Gert.Model.Events;

namespace Gert.TurnControl.Local;

/// <summary>
/// The in-process <see cref="ITurnControlBus"/> (the default <c>Local</c> control plane): a per-scope
/// registry of the one live turn subscription, wired straight to it in memory. Single-instance - the
/// agent host and the chat API are the same process, so a publish reaches the runner with no transport.
/// This is the degenerate one-process case of the contract a networked bus would satisfy across
/// instances. The per-conversation streaming gate guarantees at most one live turn per scope, so one slot.
/// </summary>
internal sealed class InProcessTurnControlBus : ITurnControlBus
{
    private readonly TimeProvider _clock;

    // The live turn per scope. Last writer wins (a crashed turn's stale entry is replaced on the next
    // subscribe); a disposed subscription removes only its own entry (pair-equality remove).
    private readonly ConcurrentDictionary<ControlScope, LocalSubscription> _live = new();

    // Cancels published with no live subscriber yet (the turn is still queued), retained with their stamp
    // so the runner catches them when it subscribes. Consumed by the next SubscribeAsync for the scope; a
    // stamp older than that turn's `since` is dropped, not honoured (the freshness boundary).
    private readonly ConcurrentDictionary<ControlScope, DateTimeOffset> _pendingCancels = new();

    public InProcessTurnControlBus(TimeProvider clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc />
    public Task<ITurnControlSubscription> SubscribeAsync(
        ControlScope scope,
        DateTimeOffset since,
        CancellationToken cancellationToken = default)
    {
        var subscription = new LocalSubscription(scope, this);
        _live[scope] = subscription;

        // A cancel that raced ahead of the runner (published while the turn was queued) trips it now; a
        // leftover cancel from a PRIOR turn of this conversation (stamp < since) is consumed and dropped.
        if (_pendingCancels.TryRemove(scope, out var stamp) && stamp >= since)
        {
            subscription.TripCancel();
        }

        return Task.FromResult<ITurnControlSubscription>(subscription);
    }

    /// <inheritdoc />
    public Task PublishCancelAsync(ControlScope scope, CancellationToken cancellationToken = default)
    {
        if (_live.TryGetValue(scope, out var subscription))
        {
            subscription.TripCancel();
        }
        else
        {
            // No live turn (still queued, or already finished): retain so a turn that subscribes next
            // catches it. A finished turn's scope is only reused by a later turn whose newer `since`
            // drops this stale stamp, so this never leaks past the next turn of the conversation.
            _pendingCancels[scope] = _clock.GetUtcNow();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<AnswerOutcome> SubmitAnswerAsync(
        ControlScope scope,
        string questionId,
        IReadOnlyList<string> answers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(questionId);
        ArgumentNullException.ThrowIfNull(answers);

        // A scope that names no live turn (wrong user key, finished/never-started turn) is
        // NoSuchQuestion -> 404, the same answer a stale question id gets: tenant isolation falls out of
        // the token-derived UserKey in the scope, not a separate check.
        var outcome = _live.TryGetValue(scope, out var subscription)
            ? subscription.TryAnswer(questionId, answers)
            : AnswerOutcome.NoSuchQuestion;

        return Task.FromResult(outcome);
    }

    private void Remove(ControlScope scope, LocalSubscription subscription) =>
        _live.TryRemove(new KeyValuePair<ControlScope, LocalSubscription>(scope, subscription));

    /// <summary>One live turn's channel: its cancel source and its open ask_user questions.</summary>
    private sealed class LocalSubscription : ITurnControlSubscription
    {
        private readonly ControlScope _scope;
        private readonly InProcessTurnControlBus _bus;
        private readonly CancellationTokenSource _cancel = new();
        private readonly ConcurrentDictionary<string, AnswerSlot> _questions = new(StringComparer.Ordinal);

        public LocalSubscription(ControlScope scope, InProcessTurnControlBus bus)
        {
            _scope = scope;
            _bus = bus;
        }

        public CancellationToken Cancelled => _cancel.Token;

        public void TripCancel()
        {
            try
            {
                _cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The turn ended (subscription disposed) just as the cancel arrived - nothing to stop.
            }
        }

        public AnswerOutcome TryAnswer(string questionId, IReadOnlyList<string> answers)
        {
            if (!_questions.TryGetValue(questionId, out var slot))
            {
                return AnswerOutcome.NoSuchQuestion;
            }

            if (!AnswerValidation.Fits(slot.Questions, answers))
            {
                return AnswerOutcome.Invalid;
            }

            // Deliver once; a duplicate submit (TrySetResult == false) is still a clean accept for the user.
            slot.Completion.TrySetResult(answers);
            return AnswerOutcome.Accepted;
        }

        public Task OpenQuestionAsync(
            string questionId,
            IReadOnlyList<AskedQuestion> questions,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(questionId);
            ArgumentNullException.ThrowIfNull(questions);

            _questions[questionId] = new AnswerSlot(questions);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> WaitForAnswerAsync(
            string questionId,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(questionId);

            if (!_questions.TryGetValue(questionId, out var slot))
            {
                // Opened-then-closed before the wait began: end only on the caller's deadline / cancel.
                return AwaitCancellationAsync(cancellationToken);
            }

            return slot.Completion.Task.WaitAsync(cancellationToken);
        }

        public Task CloseQuestionAsync(string questionId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(questionId);

            _questions.TryRemove(questionId, out _);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            // Deregister BEFORE disposing the source, so a concurrent publish/submit observes "no live
            // turn" rather than racing the cancel disposal (TripCancel swallows the disposed-race window).
            _bus.Remove(_scope, this);
            _cancel.Dispose();
            return ValueTask.CompletedTask;
        }

        private static async Task<IReadOnlyList<string>> AwaitCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return []; // unreachable: the infinite delay only ends by cancellation, which throws
        }
    }

    /// <summary>One open question: its asked shape (for answer validation) and the waiter's completion.</summary>
    private sealed class AnswerSlot
    {
        public AnswerSlot(IReadOnlyList<AskedQuestion> questions)
        {
            Questions = questions;
        }

        public IReadOnlyList<AskedQuestion> Questions { get; }

        public TaskCompletionSource<IReadOnlyList<string>> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
