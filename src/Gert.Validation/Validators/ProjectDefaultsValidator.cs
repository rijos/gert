using FluentValidation;
using Gert.Model.Projects;
using Gert.Validation.Rules;

namespace Gert.Validation.Validators;

/// <summary>
/// Validates the project-level <see cref="ProjectDefaults"/> cascade entry
/// (configuration.md section 1): every field optional, but a supplied model id / reply
/// language must be a safe token and nested tools must clear their own validator.
/// </summary>
public sealed class ProjectDefaultsValidator : AbstractValidator<ProjectDefaults>
{
    public ProjectDefaultsValidator(ToolTogglesValidator toolsValidator)
    {
        ArgumentNullException.ThrowIfNull(toolsValidator);

        RuleFor(d => d.ModelId!)
            .Must(ValidationRules.IsSafeIdentifier)
            .When(d => d.ModelId is not null)
            .WithMessage("Model id must be a safe identifier token.")
            .WithErrorCode("model_id.invalid");

        // TODO: allowlist model_id against the provider catalog when it lands.
        RuleFor(d => d.ReplyLanguage!)
            .Must(ValidationRules.IsSafeIdentifier)
            .When(d => d.ReplyLanguage is not null)
            .WithMessage("Reply language must be a safe BCP-47 tag.")
            .WithErrorCode("reply_language.invalid");

        RuleFor(d => d.Tools!).SetValidator(toolsValidator).When(d => d.Tools is not null);
    }
}
