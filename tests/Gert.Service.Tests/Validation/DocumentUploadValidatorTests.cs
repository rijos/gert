using FluentValidation;
using FluentValidation.TestHelper;
using Gert.Service.Documents;
using Gert.Service.Validation;
using Gert.Service.Validation.Validators;
using Xunit;

namespace Gert.Service.Tests.Validation;

/// <summary>
/// Tests for the relaxed <see cref="DocumentUpload"/> gate (U7d decision): the
/// filename is display metadata, base64-stored and render-sanitized by the SPA — it
/// is <b>not</b> a storage path. So the gate is extension allowlist + size + length
/// + non-empty + content-type only; exotic filenames (separators, <c>..</c>, bidi,
/// control chars) are <b>preserved</b> as long as the extension is allowed, while a
/// disallowed extension / empty / oversized upload still fails.
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
    [InlineData("a.pdf")]
    [InlineData("a.docx")]
    [InlineData("a.md")]
    [InlineData("a.txt")]
    public void Allowed_extensions_pass(string filename) =>
        _validator.TestValidate(Upload(filename: filename, mime: "text/plain"))
            .ShouldNotHaveValidationErrorFor(u => u.Filename);

    // The decision: the upload name is metadata, not a path. Exotic names that the
    // old path-safety rule rejected are now PRESERVED — as long as the extension is
    // allowed. (Path traversal is impossible by construction: the blob is stored
    // under a server-generated {doc-id}.{ext} key, never this name.)
    [Theory]
    [InlineData("../etc/passwd.pdf")]
    [InlineData("foo/bar.pdf")]
    [InlineData("foo\\bar.pdf")]
    [InlineData("..weird...name.txt")]
    [InlineData("C:\\Windows\\system32\\evil.txt")]
    public void Exotic_filenames_with_an_allowed_extension_are_preserved(string filename) =>
        _validator.TestValidate(Upload(filename: filename, mime: "text/plain"))
            .ShouldNotHaveValidationErrorFor(u => u.Filename);

    [Fact]
    public void Bidi_and_control_char_filenames_are_preserved_when_the_extension_is_allowed()
    {
        // RTL-override spoof and an embedded control char: the old gate rejected
        // these; the new gate preserves them (display safety is the SPA's job).
        var rtlSpoof = "apple" + (char)0x202E + "txt.exe.pdf";
        var withControl = "note" + (char)0x0007 + "bell.md";

        _validator.TestValidate(Upload(filename: rtlSpoof, mime: "application/pdf"))
            .ShouldNotHaveValidationErrorFor(u => u.Filename);
        _validator.TestValidate(Upload(filename: withControl, mime: "text/plain"))
            .ShouldNotHaveValidationErrorFor(u => u.Filename);
    }

    [Theory]
    [InlineData("doc.exe")]
    [InlineData("doc.js")]
    [InlineData("noext")]
    [InlineData("../etc/passwd")] // disallowed because no allowed extension, not because of '..'
    public void Disallowed_extension_fails(string filename) =>
        _validator.TestValidate(Upload(filename: filename))
            .ShouldHaveValidationErrorFor(u => u.Filename);

    [Fact]
    public void Empty_filename_fails() =>
        _validator.TestValidate(Upload(filename: string.Empty))
            .ShouldHaveValidationErrorFor(u => u.Filename);

    [Fact]
    public void Overlong_filename_fails()
    {
        var name = new string('a', DocumentUploadValidator.MaxFilenameLength) + ".pdf";
        _validator.TestValidate(Upload(filename: name))
            .ShouldHaveValidationErrorFor(u => u.Filename);
    }

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
