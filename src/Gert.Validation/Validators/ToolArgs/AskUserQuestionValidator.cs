using FluentValidation;
using Gert.Tools.Args;
using Gert.Validation.Rules;

namespace Gert.Validation.Validators.ToolArgs;

/// <summary>
/// Validates one <see cref="AskUserQuestion"/>: a non-empty, bounded, control/bidi-safe
/// <c>question</c>; an optional bounded <c>header</c> and <c>options</c> set (each option a
/// non-empty, bounded, safe string); and the rule that <c>allow_free_text=false</c> only makes
/// sense alongside options. Nested under <see cref="AskUserArgsValidator"/> via <c>SetValidator</c>.
/// </summary>
public sealed class AskUserQuestionValidator : AbstractValidator<AskUserQuestion>
{
    public AskUserQuestionValidator()
    {
        RuleFor(q => q.Question)
            .Must(v => !string.IsNullOrWhiteSpace(v))
                .WithMessage("question is required")
                .WithErrorCode("ask_user.question_required")
            .Must(v => v is null || v.Length <= AskUserQuestion.MaxQuestionChars)
                .WithMessage($"question is too long (max {AskUserQuestion.MaxQuestionChars} characters)")
                .WithErrorCode("ask_user.question_too_long")
            .Must(v => !ValidationRules.ContainsForbiddenControlChar(v))
                .WithMessage("question must not contain control characters.")
                .WithErrorCode("ask_user.question_control_char")
            .Must(v => !ValidationRules.ContainsBidiOverride(v))
                .WithMessage("question must not contain bidirectional-override characters.")
                .WithErrorCode("ask_user.question_bidi_override");

        // A header is optional and may be blank (the tool drops a blank one); only its
        // length and character bar are enforced when one is supplied. Length is checked
        // on the TRIMMED value (the tool trims before display), so surrounding whitespace
        // never tips a within-cap label over the limit.
        RuleFor(q => q.Header!)
            .Must(v => v.Trim().Length <= AskUserQuestion.MaxHeaderChars)
                .WithMessage($"header is too long (max {AskUserQuestion.MaxHeaderChars} characters)")
                .WithErrorCode("ask_user.header_too_long")
            .Must(v => !ValidationRules.ContainsForbiddenControlChar(v))
                .WithMessage("header must not contain control characters.")
                .WithErrorCode("ask_user.header_control_char")
            .Must(v => !ValidationRules.ContainsBidiOverride(v))
                .WithMessage("header must not contain bidirectional-override characters.")
                .WithErrorCode("ask_user.header_bidi_override")
            .When(q => q.Header is not null);

        RuleFor(q => q.Options!)
            .Must(o => o.Count <= AskUserQuestion.MaxOptions)
                .WithMessage($"too many options (max {AskUserQuestion.MaxOptions})")
                .WithErrorCode("ask_user.too_many_options")
            .When(q => q.Options is not null);

        RuleForEach(q => q.Options!)
            .Must(o => !string.IsNullOrWhiteSpace(o))
                .WithMessage("options must be non-empty strings")
                .WithErrorCode("ask_user.option_empty")
            .Must(o => o is null || o.Length <= AskUserQuestion.MaxOptionChars)
                .WithMessage($"an option is too long (max {AskUserQuestion.MaxOptionChars} characters)")
                .WithErrorCode("ask_user.option_too_long")
            .Must(o => !ValidationRules.ContainsForbiddenControlChar(o))
                .WithMessage("options must not contain control characters.")
                .WithErrorCode("ask_user.option_control_char")
            .Must(o => !ValidationRules.ContainsBidiOverride(o))
                .WithMessage("options must not contain bidirectional-override characters.")
                .WithErrorCode("ask_user.option_bidi_override")
            .When(q => q.Options is not null);

        // Mirrors the schema default: free text is the norm for an open question, so turning
        // it off only makes sense when a closed option set was offered.
        RuleFor(q => q)
            .Must(q => !(q.AllowFreeText == false && (q.Options is null || q.Options.Count == 0)))
                .WithMessage("allow_free_text=false requires options")
                .WithErrorCode("ask_user.free_text_requires_options");
    }
}
