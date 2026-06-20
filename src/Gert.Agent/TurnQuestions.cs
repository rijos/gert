using System.Collections.Concurrent;
using Gert.Model.Dtos;
using Gert.Validation;

namespace Gert.Agent;

/// <summary>
/// <see cref="ITurnQuestions"/> - a singleton map of pending questions keyed by
/// <see cref="TurnKey"/>, mirroring <see cref="TurnCancellation"/> (same
/// in-process rationale, same release semantics). Each entry wraps a
/// <see cref="TaskCompletionSource{TResult}"/>; every race is settled by the
/// TCS's single-transition guarantee:
/// <list type="bullet">
///   <item>answer vs timeout/cancel - the wait seals the TCS before reporting
///   "no response", so a losing answer returns <see cref="AnswerOutcome.NotFound"/>
///   instead of being silently dropped;</item>
///   <item>answer vs dispose - dispose seals too, and removes only its OWN
///   registration (a successor turn may have re-registered the key);</item>
///   <item>a second answer - the first transition won; NotFound.</item>
/// </list>
/// </summary>
public sealed class TurnQuestions : ITurnQuestions
{
    private readonly ConcurrentDictionary<TurnKey, Pending> _pending = new();

    /// <inheritdoc />
    public IPendingQuestion Open(TurnKey key, QuestionPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var pending = new Pending(this, key, payload);
        if (!_pending.TryAdd(key, pending))
        {
            throw new QuestionAlreadyPendingException();
        }

        return pending;
    }

    /// <inheritdoc />
    public AnswerOutcome Answer(TurnKey key, Validated<AnswerRequest> request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var dto = request.Value;

        if (!_pending.TryGetValue(key, out var pending))
        {
            return AnswerOutcome.NotFound;
        }

        if (!string.Equals(pending.QuestionId, dto.QuestionId, StringComparison.Ordinal))
        {
            return AnswerOutcome.IdMismatch;
        }

        var questions = pending.Payload.Questions;
        if (dto.Answers.Count != questions.Count)
        {
            // One answer per question or the tool would mis-pair them - the
            // validator caps the count, this enforces the match to the payload.
            return AnswerOutcome.InvalidOption;
        }

        for (var i = 0; i < questions.Count; i++)
        {
            var question = questions[i];
            if (!question.AllowFreeText
                && question.Options.Count > 0
                && !question.Options.Contains(dto.Answers[i], StringComparer.Ordinal))
            {
                return AnswerOutcome.InvalidOption;
            }
        }

        // TrySetResult loses to a sealed (timed-out / cancelled / disposed)
        // question - the benign race; the caller sees NotFound, never a 202
        // for an answer the tool will not read.
        return pending.TryDeliver(dto.Answers)
            ? AnswerOutcome.Delivered
            : AnswerOutcome.NotFound;
    }

    private void Release(TurnKey key, Pending pending) =>
        // Remove only OUR registration - a successor turn for the same
        // conversation may already have re-registered under this key
        // (TurnCancellation.Release, the same pattern).
        ((ICollection<KeyValuePair<TurnKey, Pending>>)_pending)
            .Remove(new KeyValuePair<TurnKey, Pending>(key, pending));

    private sealed class Pending : IPendingQuestion
    {
        private readonly TurnQuestions _owner;
        private readonly TurnKey _key;
        private readonly TaskCompletionSource<IReadOnlyList<string>> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Pending(TurnQuestions owner, TurnKey key, QuestionPayload payload)
        {
            _owner = owner;
            _key = key;
            Payload = payload;
            QuestionId = Guid.NewGuid().ToString("D");
        }

        public string QuestionId { get; }

        public QuestionPayload Payload { get; }

        public bool TryDeliver(IReadOnlyList<string> answers) => _tcs.TrySetResult(answers);

        public async Task<IReadOnlyList<string>?> WaitAsync(TimeSpan timeout, CancellationToken token)
        {
            if (timeout < TimeSpan.Zero)
            {
                timeout = TimeSpan.Zero;
            }

            try
            {
                return await _tcs.Task.WaitAsync(timeout, token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Seal the question so a late answer can't report Delivered
                // after we already returned "no response". If an answer WON the
                // seal race, honour it - the user's 202 was truthful.
                if (_tcs.TrySetCanceled(CancellationToken.None))
                {
                    return null;
                }

                return _tcs.Task.IsCompletedSuccessfully ? _tcs.Task.Result : null;
            }
            catch (OperationCanceledException)
            {
                // Turn cancel / shutdown / budget: seal (a post-cancel answer
                // must be NotFound) and let the OCE unwind to the runner's
                // finalize.
                _tcs.TrySetCanceled(CancellationToken.None);
                throw;
            }
        }

        public void Dispose()
        {
            // Seal first, release second: once disposed the question can never
            // deliver, and the key is free for the conversation's next turn.
            _tcs.TrySetCanceled(CancellationToken.None);
            _owner.Release(_key, this);
        }
    }
}
