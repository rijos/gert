using FluentValidation;
using Gert.Model.Chat;
using Gert.Validation.Rules;

namespace Gert.Validation.Validators;

/// <summary>
/// Validates one inline image attachment (testing.md section 5: untrusted bytes at the
/// boundary): MIME from the image allowlist, base64 well-formed (checked without
/// decoding) and bounded - the per-image DoS brake behind
/// <see cref="ValidationRules.AttachmentDataMaxChars"/>.
/// </summary>
public sealed class MessageAttachmentValidator : AbstractValidator<MessageAttachment>
{
    public MessageAttachmentValidator()
    {
        RuleFor(a => a.MimeType)
            .Must(ValidationRules.IsAllowedImageMime)
            .WithMessage("Attachment MIME type must be image/png, image/jpeg, image/webp or image/gif.")
            .WithErrorCode("attachment.mime.invalid");

        RuleFor(a => a.Data)
            .Must(d => d is { Length: > 0 and <= ValidationRules.AttachmentDataMaxChars })
                .WithMessage($"Attachment data must be 1..{ValidationRules.AttachmentDataMaxChars} base64 characters.")
                .WithErrorCode("attachment.data.size")
            .Must(d => d is not { Length: > 0 and <= ValidationRules.AttachmentDataMaxChars }
                       || ValidationRules.IsWellFormedBase64(d))
                .WithMessage("Attachment data must be well-formed base64.")
                .WithErrorCode("attachment.data.base64");
    }
}
