using Gert.Model.Events;

namespace Gert.TurnControl;

/// <summary>
/// A turn's live control channel (chat-and-tools.md section detached turns), held open for the turn's
/// lifetime. The runner links <see cref="Cancelled"/> into the turn's cancellation token, declares each
/// <c>ask_user</c> question with <see cref="OpenQuestionAsync"/> so an answer can be validated and routed
/// to it, and awaits the answer with <see cref="WaitForAnswerAsync"/>. Disposing ends the subscription -
/// a later cancel/answer for the scope then finds no turn. The in-process impl is the degenerate
/// single-process case of the same contract a networked bus (Kafka/NATS) satisfies across instances.
/// </summary>
public interface ITurnControlSubscription : IAsyncDisposable
{
    /// <summary>Trips when a cancel is published for this scope - the runner links it into the turn token.</summary>
    CancellationToken Cancelled { get; }

    /// <summary>
    /// Declare the pending question so a submitted answer can be validated against its options and routed
    /// to this turn. One open question at a time (the loop runs tool calls sequentially).
    /// </summary>
    Task OpenQuestionAsync(
        string questionId,
        IReadOnlyList<AskedQuestion> questions,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Await the answer to <paramref name="questionId"/>; completes when a matching answer is accepted.
    /// The caller cancels the wait via <paramref name="cancellationToken"/> (its budget / the turn token) -
    /// a cancelled wait throws <see cref="OperationCanceledException"/>; it never returns an empty answer.
    /// </summary>
    Task<IReadOnlyList<string>> WaitForAnswerAsync(
        string questionId,
        CancellationToken cancellationToken = default);

    /// <summary>Seal the question: a later answer for it is <see cref="AnswerOutcome.NoSuchQuestion"/>.</summary>
    Task CloseQuestionAsync(string questionId, CancellationToken cancellationToken = default);
}
