using Gert.Model.Events;

namespace Gert.TurnControl;

/// <summary>
/// The closed-question fit rule (chat-and-tools.md section ask the user), shared by every
/// <see cref="ITurnControlBus"/> impl so the answer contract is single-sourced: exactly one answer per
/// asked question, and a closed question (<c>allow_free_text=false</c> with offered options) must be
/// answered with one of those options. A free-text question accepts any value (length is the body
/// validator's job, not this rule's).
/// </summary>
public static class AnswerValidation
{
    /// <summary>True when <paramref name="answers"/> is a valid reply to <paramref name="questions"/>.</summary>
    public static bool Fits(IReadOnlyList<AskedQuestion> questions, IReadOnlyList<string> answers)
    {
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentNullException.ThrowIfNull(answers);

        if (answers.Count != questions.Count)
        {
            return false;
        }

        for (var i = 0; i < questions.Count; i++)
        {
            var question = questions[i];
            if (!question.AllowFreeText
                && question.Options.Count > 0
                && !question.Options.Contains(answers[i], StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
