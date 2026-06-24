namespace Gert.Model.Events;

/// <summary>
/// <c>question_asked</c> - the <c>ask_user</c> tool opened an interactive
/// question and is now blocking the turn on the user's answer (rest-api.md SSE
/// table; chat-and-tools.md section Ask the user). Carries the FULL payload: the
/// <c>tool_call</c> event's display <c>Request</c> caps long strings, so this
/// event - not the tool card request - is the question's source of truth. It is
/// persisted before it is published like every event, so a reconnecting client
/// replays the pending question and can still answer it.
/// </summary>
public sealed record QuestionAskedEvent : ChatEvent
{
    /// <summary>The tool-call id - the SPA folds the question onto that card.</summary>
    public required string Id { get; init; }

    /// <summary>
    /// Server-minted question id, the <c>POST .../answer</c> correlator. Distinct
    /// from <see cref="Id"/>: the tool-call id comes from the model and must
    /// never address the answer registry.
    /// </summary>
    public required string QuestionId { get; init; }

    /// <summary>
    /// The questions to ask together (1..4), rendered as tabs; the answer carries
    /// one entry per question, in this order.
    /// </summary>
    public required IReadOnlyList<AskedQuestion> Questions { get; init; }

    public override ChatEventType Type => ChatEventType.QuestionAsked;
}
