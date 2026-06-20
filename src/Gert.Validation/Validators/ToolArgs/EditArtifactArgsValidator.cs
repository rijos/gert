using FluentValidation;
using Gert.Tools.Args;
using Gert.Validation.Rules;

namespace Gert.Validation.Validators.ToolArgs;

/// <summary>
/// Validates the canvas edit tool's args (<c>edit_artifact</c>): a safe, non-empty
/// <c>name</c> and a non-empty <c>old_str</c> (the verbatim match target). <c>new_str</c>
/// is optional - empty deletes the snippet - so it is unvalidated here; the
/// match-count (zero/one/many) rule stays the tool's model-correctable error.
/// </summary>
public sealed class EditArtifactArgsValidator : AbstractValidator<EditArtifactArgs>
{
    public EditArtifactArgsValidator()
    {
        RuleFor(a => a.Name).SafeText(ValidationRules.ShortTextMax);

        RuleFor(a => a.OldStr)
            .Must(v => !string.IsNullOrEmpty(v))
                .WithMessage("old_str is required")
                .WithErrorCode("old_str.empty")
            .Must(v => v is null || v.Length <= ValidationRules.LongTextMax)
                .WithMessage($"old_str must be at most {ValidationRules.LongTextMax} characters.")
                .WithErrorCode("old_str.too_long");
    }
}
