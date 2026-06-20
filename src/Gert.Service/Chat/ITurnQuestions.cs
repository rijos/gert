using Gert.Model.Dtos;
using Gert.Validation;

namespace Gert.Service.Chat;

/// <summary>
/// Process-wide registry of pending <c>ask_user</c> questions, the awaitable
/// mirror of <see cref="ITurnCancellation"/> (rest-api.md section answer a question):
/// the tool opens a question under its <see cref="TurnKey"/> and awaits it; the
/// answer endpoint delivers by the same key, built from the authenticated
/// <c>IUserContext</c> - so a caller can only ever address questions in their
/// own tenant. One pending question per turn; no tombstones - unlike a cancel,
/// an answer cannot legitimately precede its question (the client only learns
/// of it from the persisted <c>question_asked</c> event), so an early answer is
/// <see cref="AnswerOutcome.NotFound"/>. In-process only, like the cancel
/// registry: the in-process queue means the addressed turn always lives here
/// (the <c>turn_events</c> log is the cross-instance truth, but an answer must
/// reach the owning process).
/// </summary>
public interface ITurnQuestions
{
    /// <summary>
    /// Open the turn's pending question. Throws
    /// <see cref="QuestionAlreadyPendingException"/> when one is already open
    /// for <paramref name="key"/> (the tool maps it to a tool error - one
    /// question per turn). Dispose the handle when the wait ends (releases the
    /// key, seals the question against late answers).
    /// </summary>
    IPendingQuestion Open(TurnKey key, QuestionPayload payload);

    /// <summary>
    /// Deliver the answers to the pending question for <paramref name="key"/>
    /// (one answer per question, in order). <see cref="AnswerOutcome.NotFound"/>
    /// when none is pending (or it already resolved),
    /// <see cref="AnswerOutcome.IdMismatch"/> for a stale/foreign question id,
    /// <see cref="AnswerOutcome.InvalidOption"/> when the answer count does not
    /// match the question count, or when a closed question
    /// (<c>allow_free_text=false</c>) gets an answer outside its options.
    /// </summary>
    AnswerOutcome Answer(TurnKey key, Validated<AnswerRequest> request);
}

/// <summary>One open question - disposed by the asking tool when its wait ends.</summary>
public interface IPendingQuestion : IDisposable
{
    /// <summary>Server-minted question id (a GUID) - NOT the model's tool-call id.</summary>
    string QuestionId { get; }

    QuestionPayload Payload { get; }

    /// <summary>
    /// Wait for the answers: one answer per question (in question order), or
    /// null on timeout (the graceful "user did not respond" path). Cancellation
    /// of <paramref name="token"/> (user stop / shutdown / turn budget) throws
    /// <see cref="OperationCanceledException"/> - the runner's existing
    /// cancel/error finalize handles it. Timeout and cancellation both seal the
    /// question, so an answer losing the race lands as
    /// <see cref="AnswerOutcome.NotFound"/>, never silently dropped after the
    /// tool already reported no response.
    /// </summary>
    Task<IReadOnlyList<string>?> WaitAsync(TimeSpan timeout, CancellationToken token);
}

/// <summary>
/// The questions as shown to the user (the <c>question_asked</c> payload): up to
/// four questions answered together, rendered as tabs.
/// </summary>
public sealed record QuestionPayload(IReadOnlyList<QuestionItem> Questions);

/// <summary>Outcome of <see cref="ITurnQuestions.Answer"/>.</summary>
public enum AnswerOutcome
{
    /// <summary>The waiting tool received the answer (the endpoint's 202).</summary>
    Delivered,

    /// <summary>No pending question for this tenant/conversation (404).</summary>
    NotFound,

    /// <summary>A question is pending but under a different id - stale client (404).</summary>
    IdMismatch,

    /// <summary>Closed options and the answer names none of them (400).</summary>
    InvalidOption,
}
