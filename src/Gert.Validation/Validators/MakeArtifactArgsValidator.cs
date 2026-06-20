using FluentValidation;
using Gert.Tools;

namespace Gert.Validation.Validators;

/// <summary>
/// Validates the canvas create tool's args (<c>make_artifact</c>): a safe, non-empty
/// <c>name</c>, a non-empty <c>format</c> word, and non-empty <c>content</c>. Format
/// MEMBERSHIP (and its aliases like <c>py</c>/<c>md</c>) is resolved in the tool, not
/// here - the alias map is an impl detail of the builtin leaf - so this only requires
/// the word is present. Content is held to no character bar: an artifact body is an
/// opaque file (HTML/SVG/source) that legitimately carries any text.
/// </summary>
public sealed class MakeArtifactArgsValidator : AbstractValidator<MakeArtifactArgs>
{
    public MakeArtifactArgsValidator()
    {
        RuleFor(a => a.Name).SafeText(ValidationRules.ShortTextMax);

        RuleFor(a => a.Format)
            .Must(v => !string.IsNullOrWhiteSpace(v))
                .WithMessage("format is required")
                .WithErrorCode("format.empty");

        RuleFor(a => a.Content)
            .Must(v => !string.IsNullOrEmpty(v))
                .WithMessage("content is required")
                .WithErrorCode("content.empty")
            .Must(v => v is null || v.Length <= ValidationRules.LongTextMax)
                .WithMessage($"content must be at most {ValidationRules.LongTextMax} characters.")
                .WithErrorCode("content.too_long");
    }
}
