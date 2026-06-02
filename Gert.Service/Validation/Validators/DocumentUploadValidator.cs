using FluentValidation;
using Gert.Service.Documents;

namespace Gert.Service.Validation.Validators;

/// <summary>
/// Validates a <see cref="DocumentUpload"/> (testing.md §5: filename — reject
/// path separators / <c>..</c>, extension allowlist; bytes/type — max size,
/// content-type allowlist, reject empty). This is the upload-hardening gate
/// (security F-upload-storage) before bytes are written or extracted.
/// </summary>
public sealed class DocumentUploadValidator : AbstractValidator<DocumentUpload>
{
    public DocumentUploadValidator()
    {
        RuleFor(u => u.Filename)
            .Must(ValidationRules.IsSafeFilename)
                .WithMessage("Filename must be a basename with no path separators or '..'.")
                .WithErrorCode("upload.filename")
            .Must(f => UploadConstraints.AllowedExtensions.Contains(ValidationRules.ExtensionOf(f)))
                .WithMessage("File type not allowed (pdf, docx, md, txt only).")
                .WithErrorCode("upload.extension")
                // Only worth checking the extension once the filename is a clean basename.
                // CurrentValidator: gate ONLY the extension rule — the default (AllValidators)
                // would also disable the IsSafeFilename rule above for an unsafe name, letting it slip.
                .When(u => ValidationRules.IsSafeFilename(u.Filename), ApplyConditionTo.CurrentValidator);

        RuleFor(u => u.Mime)
            .NotEmpty()
                .WithMessage("Content-type is required.")
                .WithErrorCode("upload.mime_missing")
            .Must(m => UploadConstraints.AllowedMimeTypes.Contains(m.Trim().ToLowerInvariant()))
                .WithMessage("Content-type not allowed.")
                .WithErrorCode("upload.mime")
                .When(u => !string.IsNullOrWhiteSpace(u.Mime), ApplyConditionTo.CurrentValidator);

        // Size: when the host knows it up front, reject empty and over-cap before reading.
        RuleFor(u => u.SizeBytes!.Value)
            .GreaterThan(0)
                .WithMessage("Upload must not be empty.")
                .WithErrorCode("upload.empty")
            .LessThanOrEqualTo(UploadConstraints.MaxSizeBytes)
                .WithMessage($"Upload exceeds the {UploadConstraints.MaxSizeBytes} byte limit.")
                .WithErrorCode("upload.too_large")
            .When(u => u.SizeBytes.HasValue);

        RuleFor(u => u.OpenReadStream)
            .NotNull()
            .WithMessage("An upload stream is required.")
            .WithErrorCode("upload.stream_missing");
    }
}
