using FluentValidation;
using Gert.Tools.Args;

namespace Gert.Validation.Validators.ToolArgs;

/// <summary>
/// Validates the ask-user tool's args (<c>ask_user</c>): a non-empty <c>questions</c> list capped
/// at <see cref="AskUserArgs.MaxQuestions"/> (the model can't flood the user with tabs), each entry
/// a valid <see cref="AskUserQuestion"/>. The typed wire collapses a missing list and an explicit
/// empty one to the same empty collection, so both fail here - a meaningful ask names at least one.
/// </summary>
public sealed class AskUserArgsValidator : AbstractValidator<AskUserArgs>
{
    public AskUserArgsValidator()
    {
        RuleFor(a => a.Questions)
            .Must(q => q is { Count: > 0 })
                .WithMessage("at least one question is required")
                .WithErrorCode("ask_user.empty")
            .Must(q => q.Count <= AskUserArgs.MaxQuestions)
                .WithMessage($"too many questions (max {AskUserArgs.MaxQuestions})")
                .WithErrorCode("ask_user.too_many")
            // RuleForEach + SetValidator silently skips null list elements, so a null
            // question (e.g. {"questions":[null]}) would pass and then NRE in the tool;
            // reject it here as the model-correctable error the old hand-parser gave.
            .Must(q => q.All(x => x is not null))
                .WithMessage("each question must be an object")
                .WithErrorCode("ask_user.question_null");

        RuleForEach(a => a.Questions).SetValidator(new AskUserQuestionValidator());
    }
}
