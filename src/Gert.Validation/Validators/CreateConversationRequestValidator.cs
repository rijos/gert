using FluentValidation;
using Gert.Model.Dtos;
using Gert.Validation.Rules;

namespace Gert.Validation.Validators;

/// <summary>
/// Validates <see cref="CreateConversationRequest"/> (rest-api.md section conversations).
/// Title is optional (a default is generated) but, when supplied, must be safe
/// short text; model id (the provider id) is a safe token; tools clear their own validator.
/// </summary>
public sealed class CreateConversationRequestValidator : AbstractValidator<CreateConversationRequest>
{
    public CreateConversationRequestValidator(ToolTogglesValidator toolsValidator)
    {
        ArgumentNullException.ThrowIfNull(toolsValidator);

        RuleFor(r => r.Title).OptionalSafeText(ValidationRules.ShortTextMax);

        RuleFor(r => r.ModelId!)
            .Must(ValidationRules.IsSafeIdentifier)
            .When(r => r.ModelId is not null)
            .WithMessage("Model id must be a safe identifier token.")
            .WithErrorCode("model_id.invalid");

        // TODO: allowlist model_id against the provider catalog when it lands.
        RuleFor(r => r.Tools!).SetValidator(toolsValidator).When(r => r.Tools is not null);
    }
}
