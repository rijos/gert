namespace Gert.Model.Dtos;

/// <summary>
/// Body of <c>POST /api/projects/{pid}/conversations/{id}/answer</c>
/// (rest-api.md section answer a question): <c>{ question_id, answer }</c> - deliver
/// the user's answer to the in-flight turn's pending <c>ask_user</c> question.
/// <see cref="QuestionId"/> is the server-minted id from the
/// <c>question_asked</c> event, never the model's tool-call id.
/// </summary>
public sealed record AnswerRequest
{
    public required string QuestionId { get; init; }

    public required string Answer { get; init; }
}
