using FluentValidation;
using Gert.Model.Dtos;

namespace Gert.Validation.Validators;

/// <summary>
/// Validates a partial <see cref="UpdateConversationRequest"/> (rest-api.md
/// section conversations): every field optional (PATCH), but a supplied title must be
/// safe short text, a supplied model id (the provider id) a safe token, and nested
/// tools clear their validator. <c>Archived</c> is a plain bool - nothing to abuse.
/// </summary>
public sealed class UpdateConversationRequestValidator : AbstractValidator<UpdateConversationRequest>
{
    public UpdateConversationRequestValidator(ToolTogglesValidator toolsValidator)
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
