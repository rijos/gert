using FluentValidation;
using Gert.Tools.Args;
using Gert.Validation.Rules;

namespace Gert.Validation.Validators.ToolArgs;

/// <summary>
/// Validates one <see cref="TodoArg"/>: a non-empty, safe <c>text</c> and a
/// <c>status</c> drawn from the known set (<c>pending</c>/<c>active</c>/<c>done</c>),
/// so an unknown status is a model-correctable error before the tool maps it onto
/// <c>TodoStatus</c>. Nested under <see cref="TodoArgsValidator"/> via <c>SetValidator</c>.
/// </summary>
public sealed class TodoArgValidator : AbstractValidator<TodoArg>
{
    private static readonly HashSet<string> KnownStatuses =
        new(["pending", "active", "done"], StringComparer.Ordinal);

    public TodoArgValidator()
    {
        RuleFor(t => t.Text)
            .Must(v => !string.IsNullOrWhiteSpace(v))
                .WithMessage("every todo needs a non-empty 'text'")
                .WithErrorCode("todo.text_empty")
            .Must(v => v is null || v.Length <= ValidationRules.ShortTextMax)
                .WithMessage($"a todo's text must be at most {ValidationRules.ShortTextMax} characters.")
                .WithErrorCode("todo.text_too_long")
            .Must(v => !ValidationRules.ContainsForbiddenControlChar(v))
                .WithMessage("a todo's text must not contain control characters.")
                .WithErrorCode("todo.text_control_char")
            .Must(v => !ValidationRules.ContainsBidiOverride(v))
                .WithMessage("a todo's text must not contain bidirectional-override characters.")
                .WithErrorCode("todo.text_bidi_override");

        RuleFor(t => t.Status)
            .Must(s => KnownStatuses.Contains(s))
                .WithMessage("every todo needs a 'status' of pending|active|done")
                .WithErrorCode("todo.status_invalid");
    }
}
