using FluentValidation;
using Gert.Model.Dtos;

namespace Gert.Service.Validation.Validators;

/// <summary>
/// Validates the chat message body (testing.md §5: message text — max length;
/// reject null/whitespace-only; refuse control &amp; bidi-override chars; model id
/// — safe token; tools — registered ids). This is the hot path: the first place
/// untrusted chat content meets the system.
/// </summary>
public sealed class SendMessageRequestValidator : AbstractValidator<SendMessageRequest>
{
    public SendMessageRequestValidator(ToolTogglesValidator toolsValidator)
    {
        ArgumentNullException.ThrowIfNull(toolsValidator);

        RuleFor(r => r.Content).SafeText(ValidationRules.LongTextMax);

        RuleFor(r => r.ModelId!)
            .Must(ValidationRules.IsSafeIdentifier)
            .When(r => r.ModelId is not null)
            .WithMessage("Model id must be a safe identifier token.")
            .WithErrorCode("model_id.invalid");

        // TODO: allowlist model_id against the model catalog when it lands.
        RuleFor(r => r.Tools!).SetValidator(toolsValidator).When(r => r.Tools is not null);
    }
}
