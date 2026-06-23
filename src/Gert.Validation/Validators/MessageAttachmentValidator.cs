using FluentValidation;
using Gert.Model.Chat;
using Gert.Validation.Rules;

namespace Gert.Validation.Validators;

/// <summary>
/// Validates one inline attachment (testing.md section 5: untrusted bytes at the boundary). Two
/// kinds, by MIME (<see cref="AttachmentKinds.IsAllowedImageMime"/> - the same gate
/// <c>TurnPlanner</c> routes on, so the validator and the prompt path agree):
/// <list type="bullet">
///   <item><b>Image</b> - an allowlisted image MIME (<see cref="ValidationRules.IsAllowedImageMime"/>),
///   the only kind that rides to a vision model as a binary part.</item>
///   <item><b>Text file</b> - any non-allowlisted MIME (including a non-allowlisted <c>image/*</c>
///   like <c>image/svg+xml</c>), but it <b>must carry a filename</b> (so an arbitrary binary cannot
///   pose as an untyped attachment); whether the bytes are really text is decided at prompt-injection
///   (the server-side text gate), not here.</item>
/// </list>
/// In both cases the base64 <see cref="MessageAttachment.Data"/> is well-formed and bounded by
/// <see cref="ValidationRules.AttachmentDataMaxChars"/> (the per-attachment DoS brake).
/// </summary>
public sealed class MessageAttachmentValidator : AbstractValidator<MessageAttachment>
{
    public MessageAttachmentValidator()
    {
        // Kind gate: an image must use an allowed image MIME; anything else must be a named file.
        RuleFor(a => a)
            .Must(a => ValidationRules.IsAllowedImageMime(a.MimeType) || !string.IsNullOrEmpty(a.Name))
            .WithMessage("Attachment must be an allowed image (image/png, image/jpeg, image/webp, image/gif) "
                + "or a named text file.")
            .WithErrorCode("attachment.kind");

        // A text-file attachment's filename is metadata, not a path (length brake only).
        RuleFor(a => a.Name!)
            .Must(n => n.Length <= ValidationRules.AttachmentNameMaxChars)
            .WithMessage($"Attachment filename must be at most {ValidationRules.AttachmentNameMaxChars} characters.")
            .WithErrorCode("attachment.name.too_long")
            .When(a => !string.IsNullOrEmpty(a.Name));

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
