using FluentValidation;
using Gert.Model.Dtos;

namespace Gert.Service.Validation.Validators;

/// <summary>
/// Validates a partial <see cref="UpdateConversationRequest"/> (rest-api.md
/// § conversations): every field optional (PATCH), but a supplied title must be
/// safe short text, a supplied model id a safe token, and nested tools/params
/// clear their validators. <c>Archived</c> is a plain bool — nothing to abuse.
/// </summary>
public sealed class UpdateConversationRequestValidator : AbstractValidator<UpdateConversationRequest>
{
    public UpdateConversationRequestValidator(
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
