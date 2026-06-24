namespace Gert.Model.Dtos;

/// <summary>
/// Body of <c>POST /api/projects/{pid}/conversations/{id}/answer</c>
/// (rest-api.md section answer a question): <c>{ question_id, answers }</c> -
/// deliver the user's answers to the in-flight turn's pending <c>ask_user</c>
/// question (one entry per asked question, in order).
/// <see cref="QuestionId"/> is the server-minted id from the
/// <c>question_asked</c> event, never the model's tool-call id.
/// </summary>
public sealed record AnswerRequest
{
    public required string QuestionId { get; init; }

    public required IReadOnlyList<string> Answers { get; init; }
}
