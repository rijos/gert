using FluentValidation;
using Gert.Model.Dtos;

namespace Gert.Service.Validation.Validators;

/// <summary>
/// Validates the chat message body (testing.md section 5): text max length, no
/// null/whitespace-only, no control or bidi-override chars; model id a safe token; tools
/// registered ids. The hot path - the first place untrusted chat content meets the system.
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
        // but supplied text is still held to the same length/character bar. A
        // null text is still missing (the DTO contract is non-null) - reported
        // accurately, not as a bogus "too long".
        RuleFor(r => r.Content)
            .Must(v => v is not null)
                .WithMessage("Text must be supplied (it may be empty when attachments are present).")
                .WithErrorCode("text.missing")
            .Must(v => v is null || v.Length <= ValidationRules.LongTextMax)
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

        // IANA zone ids are short slash-joined name tokens ("Europe/Amsterdam",
        // "Etc/GMT+2"); shape-check here, resolution stays the clock tool's
        // graceful unknown-zone error.
        RuleFor(r => r.Timezone!)
            .Must(t => t.Length <= 64 && t.All(c =>
                char.IsAsciiLetterOrDigit(c) || c is '/' or '_' or '+' or '-' or '.'))
            .When(r => !string.IsNullOrEmpty(r.Timezone))
            .WithMessage("Timezone must be a plausible IANA zone id.")
            .WithErrorCode("timezone.invalid");
    }
}
