using FluentValidation;
using Gert.Model.Documents;

namespace Gert.Validation.Validators;

/// <summary>
/// Validates a <see cref="DocumentUpload"/>: extension allowlist + size + length +
/// non-empty + content-type only. The filename is display metadata, not a storage path -
/// the blob lands under a server-generated <c>files/{doc-id}</c> key (no extension) and
/// the original name is base64'd into <c>documents.filename</c>. Because the filename
/// never reaches a storage path, this gate does <b>not</b> sanitize it for path traversal:
/// exotic names are preserved and display safety is the SPA's job (text node + bidi-isolate).
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
