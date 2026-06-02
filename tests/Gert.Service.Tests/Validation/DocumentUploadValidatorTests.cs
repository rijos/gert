using FluentValidation;
using FluentValidation.TestHelper;
using Gert.Service.Documents;
using Gert.Service.Validation;
using Xunit;

namespace Gert.Service.Tests.Validation;

/// <summary>
/// Positive / negative / boundary tests for <see cref="DocumentUpload"/>
/// validation — the upload-hardening gate (filename traversal, extension &amp;
/// MIME allowlist, size caps; testing.md section 5).
/// </summary>
public sealed class DocumentUploadValidatorTests
{
    private readonly IValidator<DocumentUpload> _validator =
        ValidationTestHost.Build().Validator<DocumentUpload>();

    private static DocumentUpload Upload(
        string filename = "doc.pdf",
        string mime = "application/pdf",
        long? size = 1024) =>
        new()
        {
            Filename = filename,
            Mime = mime,
            OpenReadStream = () => Stream.Null,
            SizeBytes = size,
        };

    [Fact]
    public void Valid_pdf_upload_passes() =>
        _validator.TestValidate(Upload()).ShouldNotHaveAnyValidationErrors();

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("foo/bar.pdf")]
    [InlineData("foo\\bar.pdf")]
    [InlineData("..")]
    public void Path_traversal_filename_fails(string filename) =>
        _validator.TestValidate(Upload(filename: filename))
            .ShouldHaveValidationErrorFor(u => u.Filename);

    [Theory]
    [InlineData("doc.exe")]
    [InlineData("doc.js")]
    [InlineData("noext")]
    public void Disallowed_extension_fails(string filename) =>
        _validator.TestValidate(Upload(filename: filename))
            .ShouldHaveValidationErrorFor(u => u.Filename);

    [Theory]
    [InlineData("a.pdf")]
    [InlineData("a.docx")]
    [InlineData("a.md")]
    [InlineData("a.txt")]
    public void Allowed_extensions_pass(string filename) =>
        _validator.TestValidate(Upload(filename: filename, mime: "text/plain"))
            .ShouldNotHaveValidationErrorFor(u => u.Filename);

    [Fact]
    public void Disallowed_mime_fails() =>
        _validator.TestValidate(Upload(mime: "application/x-msdownload"))
            .ShouldHaveValidationErrorFor(u => u.Mime);

    [Fact]
    public void Empty_upload_fails() =>
        _validator.TestValidate(Upload(size: 0))
            .ShouldHaveValidationErrorFor(u => u.SizeBytes!.Value);

    [Fact]
    public void Size_at_the_limit_passes_and_one_over_fails()
    {
        _validator.TestValidate(Upload(size: UploadConstraints.MaxSizeBytes))
            .ShouldNotHaveValidationErrorFor(u => u.SizeBytes!.Value);
        _validator.TestValidate(Upload(size: UploadConstraints.MaxSizeBytes + 1))
            .ShouldHaveValidationErrorFor(u => u.SizeBytes!.Value);
    }

    [Fact]
    public void Unknown_size_is_accepted_for_streamed_uploads() =>
        _validator.TestValidate(Upload(size: null))
            .ShouldNotHaveValidationErrorFor(u => u.SizeBytes!.Value);
}
