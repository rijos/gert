using FluentValidation;
using Gert.Model.Dtos;

namespace Gert.Service.Validation.Validators;

/// <summary>
/// Validates <see cref="AnswerRequest"/> (rest-api.md § answer a question):
/// the question id must be GUID-shaped — the registry mints ids with
/// <c>Guid.NewGuid().ToString("D")</c>, so anything else can only be noise —
/// and the answer is human-authored text held to the common safe-text bar with
/// its own cap (it is echoed into the model prompt and the event log).
/// </summary>
public sealed class AnswerRequestValidator : AbstractValidator<AnswerRequest>
{
    /// <summary>Cap on one answer — generous for free text, a DoS brake.</summary>
    public const int AnswerMaxChars = 4_000;

    public AnswerRequestValidator()
    {
        RuleFor(r => r.QuestionId)
            .Must(ValidationRules.IsWellFormedId)
                .WithMessage("Must be a canonical GUID.")
                .WithErrorCode("answer.question_id");

        RuleFor(r => r.Answer).SafeText(AnswerMaxChars);
    }
}
