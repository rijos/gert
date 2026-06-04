using FluentValidation;
using Gert.Service.Documents;

namespace Gert.Service.Validation.Validators;

/// <summary>
/// Validates a <see cref="DocumentUpload"/> — the upload gate is <b>extension
/// allowlist + size + length + non-empty + content-type</b> only (decision: the
/// filename is display metadata, not a storage path). The blob is stored under a
/// server-generated <c>{doc-id}.{ext}</c> key and the original name is base64'd into
/// <c>documents.filename</c>; display safety is the SPA's job (text node +
/// bidi-isolate), so this gate does <b>not</b> sanitize the filename for path
/// traversal — exotic names are preserved. (<see cref="ValidationRules.IsSafeFilename"/>
/// remains for any genuine path use, but the upload gate no longer calls it.)
/// </summary>
public sealed class DocumentUploadValidator : AbstractValidator<DocumentUpload>
{
    /// <summary>Cap on the original-name length (a basename DoS / metadata brake), not a path check.</summary>
    public const int MaxFilenameLength = 255;

    public DocumentUploadValidator()
    {
        RuleFor(u => u.Filename)
            .NotEmpty()
                .WithMessage("A filename is required.")
                .WithErrorCode("upload.filename_missing")
            .Must(f => f is null || f.Length <= MaxFilenameLength)
                .WithMessage($"Filename must be at most {MaxFilenameLength} characters.")
                .WithErrorCode("upload.filename_too_long")
            .Must(f => UploadConstraints.AllowedExtensions.Contains(ValidationRules.ExtensionOf(f)))
                .WithMessage("File type not allowed (pdf, docx, md, txt only).")
                .WithErrorCode("upload.extension")
                // Only meaningful once a non-empty name exists; gate ONLY this rule.
                .When(u => !string.IsNullOrEmpty(u.Filename), ApplyConditionTo.CurrentValidator);

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
