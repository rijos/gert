using FluentValidation;
using Gert.Model.Dtos;
using Gert.Validation.Rules;

namespace Gert.Validation.Validators;

/// <summary>
/// Validates <see cref="AnswerRequest"/> (rest-api.md section answer a question):
/// the question id must be GUID-shaped - the registry mints ids with
/// <c>Guid.NewGuid().ToString("D")</c>, so anything else can only be noise - and
/// the answers are human-authored text (one per asked question, 1..4) each held
/// to the common safe-text bar with its own cap (they are echoed into the model
/// prompt and the event log). Per-question option membership is a runtime check
/// in <c>TurnQuestions</c> - the validator has no access to the pending payload.
/// </summary>
public sealed class AnswerRequestValidator : AbstractValidator<AnswerRequest>
{
    /// <summary>Cap on one answer - generous for free text, a DoS brake.</summary>
    public const int AnswerMaxChars = 4_000;

    /// <summary>Max answers in one body - mirrors the four-question cap.</summary>
    public const int MaxAnswers = 4;

    public AnswerRequestValidator()
    {
        RuleFor(r => r.QuestionId)
            .Must(ValidationRules.IsWellFormedId)
                .WithMessage("Must be a canonical GUID.")
                .WithErrorCode("answer.question_id");

        RuleFor(r => r.Answers)
            .NotNull()
                .WithMessage("Answers must be supplied.")
                .WithErrorCode("answer.answers")
            .Must(a => a is null || (a.Count >= 1 && a.Count <= MaxAnswers))
                .WithMessage($"Must carry between 1 and {MaxAnswers} answers.")
                .WithErrorCode("answer.answers_count");

        RuleForEach(r => r.Answers).SafeText(AnswerMaxChars);
    }
}
