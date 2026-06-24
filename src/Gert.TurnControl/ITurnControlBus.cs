namespace Gert.TurnControl;

/// <summary>
/// The turn control plane (chat-and-tools.md section detached turns): the seam between the request side
/// (the cancel/answer endpoints) and the turn runner, decoupled from the process that owns the turn. The
/// runner <see cref="SubscribeAsync">subscribes</see> for the turn's lifetime; the endpoints
/// <see cref="PublishCancelAsync">publish a cancel</see> or <see cref="SubmitAnswerAsync">submit an
/// answer</see> to the scope WITHOUT knowing which instance runs the turn ("throw it out there" - a live
/// subscription picks it up, or nobody does). The in-process impl (Gert.TurnControl.Local) is the
/// single-process default; a networked impl (Kafka/NATS) lets the agent host and the chat API run as
/// separate deployments behind this same contract.
/// </summary>
public interface ITurnControlBus
{
    /// <summary>
    /// Open the turn's control channel. A cancel published at/after <paramref name="since"/> but before
    /// this call - the turn was still queued when the user hit stop - trips the returned subscription
    /// immediately; an older cancel (from a prior turn of the same conversation) is ignored, the freshness
    /// boundary. Pass the turn's plan instant as <paramref name="since"/>.
    /// </summary>
    Task<ITurnControlSubscription> SubscribeAsync(
        ControlScope scope,
        DateTimeOffset since,
        CancellationToken cancellationToken = default);

    /// <summary>Signal a user stop to the turn at <paramref name="scope"/> (a no-op when none is live).</summary>
    Task PublishCancelAsync(ControlScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validate and deliver an <c>ask_user</c> answer to the turn at <paramref name="scope"/>, returning
    /// the <see cref="AnswerOutcome"/> the endpoint maps to 202/404/400. Validation against the open
    /// question is the bus's job, so the endpoint stays a thin transport boundary.
    /// </summary>
    Task<AnswerOutcome> SubmitAnswerAsync(
        ControlScope scope,
        string questionId,
        IReadOnlyList<string> answers,
        CancellationToken cancellationToken = default);
}
