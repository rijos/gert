using FluentValidation;
using Gert.Tools.Args;
using Gert.Validation.Rules;

namespace Gert.Validation.Validators.ToolArgs;

/// <summary>
/// Validates the document read tool's args (<c>read_document</c>): a <see cref="ReadDocumentArgs.Doc"/>
/// reference held to the safe-text bar when supplied (an empty reference is legitimate - it lists the
/// available documents), and non-negative paging bounds. The tool clamps the window to its own cap;
/// this only rejects nonsensical inputs (negative offset / non-positive max).
/// </summary>
public sealed class ReadDocumentArgsValidator : AbstractValidator<ReadDocumentArgs>
{
    public ReadDocumentArgsValidator()
    {
        RuleFor(a => a.Doc)
            .SafeText(ValidationRules.MediumTextMax)
            .When(a => !string.IsNullOrEmpty(a.Doc));

        RuleFor(a => a.Offset!.Value)
            .GreaterThanOrEqualTo(0)
            .When(a => a.Offset is not null)
            .WithMessage("offset must not be negative.")
            .WithErrorCode("read_document.offset_negative");

        RuleFor(a => a.MaxChars!.Value)
            .GreaterThan(0)
            .When(a => a.MaxChars is not null)
            .WithMessage("max_chars must be positive.")
            .WithErrorCode("read_document.max_chars_invalid");
    }
}
