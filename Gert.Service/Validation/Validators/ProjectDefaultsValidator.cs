using FluentValidation;
using Gert.Model.Projects;

namespace Gert.Service.Validation.Validators;

/// <summary>
/// Validates the project-level <see cref="ProjectDefaults"/> cascade entry
/// (configuration.md §1): every field optional, but a supplied model id / reply
/// language must be a safe token and nested tools/params must clear their own
/// validators.
/// </summary>
public sealed class ProjectDefaultsValidator : AbstractValidator<ProjectDefaults>
{
    public ProjectDefaultsValidator(
        ToolTogglesValidator toolsValidator,
        GenerationParamsValidator paramsValidator)
    {
        ArgumentNullException.ThrowIfNull(toolsValidator);
        ArgumentNullException.ThrowIfNull(paramsValidator);

        RuleFor(d => d.ModelId!)
            .Must(ValidationRules.IsSafeIdentifier)
            .When(d => d.ModelId is not null)
            .WithMessage("Model id must be a safe identifier token.")
            .WithErrorCode("model_id.invalid");

        // TODO: allowlist model_id against the model catalog when it lands.
        RuleFor(d => d.ReplyLanguage!)
            .Must(ValidationRules.IsSafeIdentifier)
            .When(d => d.ReplyLanguage is not null)
            .WithMessage("Reply language must be a safe BCP-47 tag.")
            .WithErrorCode("reply_language.invalid");

        RuleFor(d => d.Tools!).SetValidator(toolsValidator).When(d => d.Tools is not null);
        RuleFor(d => d.Params!).SetValidator(paramsValidator).When(d => d.Params is not null);
    }
}
