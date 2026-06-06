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
    public SendMessageRequestValidator(
        ToolTogglesValidator toolsValidator,
        MessageAttachmentValidator attachmentValidator)
    {
        ArgumentNullException.ThrowIfNull(toolsValidator);
        ArgumentNullException.ThrowIfNull(attachmentValidator);

        // Text-only message: the full safe-text bar (non-empty included).
        RuleFor(r => r.Content)
            .SafeText(ValidationRules.LongTextMax)
            .When(r => r.Attachments is not { Count: > 0 });

        // With attachments an empty text is fine (an image alone is a message),
        // but supplied text is still held to the same length/character bar.
        RuleFor(r => r.Content)
            .Must(v => v is not null && v.Length <= ValidationRules.LongTextMax)
                .WithMessage($"Must be at most {ValidationRules.LongTextMax} characters.")
                .WithErrorCode("text.too_long")
            .Must(v => !ValidationRules.ContainsForbiddenControlChar(v))
                .WithMessage("Must not contain control characters.")
                .WithErrorCode("text.control_char")
            .Must(v => !ValidationRules.ContainsBidiOverride(v))
                .WithMessage("Must not contain bidirectional-override characters.")
                .WithErrorCode("text.bidi_override")
            .When(r => r.Attachments is { Count: > 0 });

        RuleFor(r => r.Attachments!)
            .Must(a => a.Count <= ValidationRules.AttachmentMaxCount)
                .WithMessage($"At most {ValidationRules.AttachmentMaxCount} attachments per message.")
                .WithErrorCode("attachments.too_many")
            .When(r => r.Attachments is not null);

        RuleForEach(r => r.Attachments!)
            .SetValidator(attachmentValidator)
            .When(r => r.Attachments is not null);

        RuleFor(r => r.ModelId!)
            .Must(ValidationRules.IsSafeIdentifier)
            .When(r => r.ModelId is not null)
            .WithMessage("Model id must be a safe identifier token.")
            .WithErrorCode("model_id.invalid");

        // TODO: allowlist model_id against the model catalog when it lands.
        RuleFor(r => r.Tools!).SetValidator(toolsValidator).When(r => r.Tools is not null);
    }
}
