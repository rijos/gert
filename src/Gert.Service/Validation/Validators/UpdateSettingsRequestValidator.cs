using FluentValidation;
using Gert.Model.Dtos;

namespace Gert.Service.Validation.Validators;

/// <summary>
/// Validates a partial <see cref="UpdateSettingsRequest"/> (rest-api.md
/// § settings): every field optional. Nullable enums are intrinsically bounded;
/// the free-text language tags and default model id must be safe identifier
/// tokens, and default tools clear their own validator.
/// </summary>
public sealed class UpdateSettingsRequestValidator : AbstractValidator<UpdateSettingsRequest>
{
    public UpdateSettingsRequestValidator(
        ToolTogglesValidator toolsValidator,
        GenerationParamsValidator paramsValidator)
    {
        ArgumentNullException.ThrowIfNull(toolsValidator);
        ArgumentNullException.ThrowIfNull(paramsValidator);

        RuleFor(r => r.UiLanguage!)
            .Must(ValidationRules.IsSafeIdentifier)
            .When(r => r.UiLanguage is not null)
            .WithMessage("UI language must be a safe BCP-47 tag.")
            .WithErrorCode("ui_language.invalid");

        RuleFor(r => r.ReplyLanguage!)
            .Must(ValidationRules.IsSafeIdentifier)
            .When(r => r.ReplyLanguage is not null)
            .WithMessage("Reply language must be a safe BCP-47 tag.")
            .WithErrorCode("reply_language.invalid");

        RuleFor(r => r.DefaultModelId!)
            .Must(ValidationRules.IsSafeIdentifier)
            .When(r => r.DefaultModelId is not null)
            .WithMessage("Model id must be a safe identifier token.")
            .WithErrorCode("model_id.invalid");

        // TODO: allowlist model_id against the model catalog when it lands.
        RuleFor(r => r.DefaultTools!).SetValidator(toolsValidator).When(r => r.DefaultTools is not null);

        // Per-model params: safe-identifier keys, each value through the same
        // bounds as conversation params.
        RuleFor(r => r.ModelParams!)
            .Must(map => map.Keys.All(ValidationRules.IsSafeIdentifier))
            .When(r => r.ModelParams is not null)
            .WithMessage("Model param keys must be safe identifier tokens.")
            .WithErrorCode("model_params.key.invalid");

        RuleForEach(r => r.ModelParams!.Values)
            .SetValidator(paramsValidator)
            .When(r => r.ModelParams is not null)
            .OverridePropertyName("model_params");
    }
}
