namespace Gert.Service.Chat;

/// <summary>
/// Thrown by <see cref="ITurnQuestions.Open"/> when the turn already has a
/// pending question - the one-question-per-turn invariant. The asking tool maps
/// it to a tool error the model can read, never a torn-down turn.
/// </summary>
public sealed class QuestionAlreadyPendingException : Exception
{
    public QuestionAlreadyPendingException()
        : base("a question is already pending for this turn")
    {
    }
}
