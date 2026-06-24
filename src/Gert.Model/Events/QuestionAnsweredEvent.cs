namespace Gert.Model.Events;

/// <summary>
/// <c>question_answered</c> - the user answered a pending <c>ask_user</c>
/// question (rest-api.md SSE table). Replay rule: the presence of this event
/// (or the call's <c>tool_result</c>, which a timeout produces without it)
/// after a <c>question_asked</c> means the question is no longer pending - a
/// reconnecting client renders the resolved state instead of the inputs.
/// </summary>
public sealed record QuestionAnsweredEvent : ChatEvent
{
    /// <summary>The tool-call id - same card as the <c>question_asked</c> event.</summary>
    public required string Id { get; init; }

    /// <summary>The server-minted question id that was answered.</summary>
    public required string QuestionId { get; init; }

    /// <summary>One answer per asked question, in the order they were asked.</summary>
    public required IReadOnlyList<string> Answers { get; init; }

    public override ChatEventType Type => ChatEventType.QuestionAnswered;
}
