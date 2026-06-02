using FluentValidation;
using Gert.Model.Dtos;

namespace Gert.Service.Validation.Validators;

/// <summary>
/// Validates <see cref="CreateConversationRequest"/> (rest-api.md § conversations).
/// Title is optional (a default is generated) but, when supplied, must be safe
/// short text; model id is a safe token; tools/params clear their own validators.
/// </summary>
public sealed class CreateConversationRequestValidator : AbstractValidator<CreateConversationRequest>
{
    public CreateConversationRequestValidator(
        ToolTogglesValidator toolsValidator,
        GenerationParamsValidator paramsValidator)
    {
        ArgumentNullException.ThrowIfNull(toolsValidator);
        ArgumentNullException.ThrowIfNull(paramsValidator);

        RuleFor(r => r.Title).OptionalSafeText(ValidationRules.ShortTextMax);

        RuleFor(r => r.ModelId!)
            .Must(ValidationRules.IsSafeIdentifier)
            .When(r => r.ModelId is not null)
            .WithMessage("Model id must be a safe identifier token.")
            .WithErrorCode("model_id.invalid");

        // TODO: allowlist model_id against the model catalog when it lands.
        RuleFor(r => r.Tools!).SetValidator(toolsValidator).When(r => r.Tools is not null);
        RuleFor(r => r.Params!).SetValidator(paramsValidator).When(r => r.Params is not null);
    }
}
