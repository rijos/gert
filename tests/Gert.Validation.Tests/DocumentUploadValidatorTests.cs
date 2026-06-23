using FluentValidation;
using FluentValidation.TestHelper;
using Gert.Model.Documents;
using Gert.Service.Documents;
using Gert.Validation;
using Gert.Validation.Rules;
using Gert.Validation.Validators;
using Xunit;

namespace Gert.Validation.Tests;

/// <summary>
/// Tests for the relaxed <see cref="DocumentUpload"/> gate: Gert accepts <b>any</b> file type
/// (type safety is enforced at extraction, not here), and the filename is display metadata -
/// base64-stored, render-sanitized by the SPA, never a storage path. So the gate is size +
/// length + non-empty + content-type-present only; exotic filenames (separators, <c>..</c>,
/// bidi, control chars) and any extension are <b>preserved</b>, while an empty / oversized
/// upload or a missing filename/content-type still fails.
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

    // Gert accepts any file type: every extension (and no extension) passes the gate.
    // The formerly-rejected exe/js/noext are now legitimate uploads; text-ness is decided
    // at extraction, not from the filename.
    [Theory]
    [InlineData("a.pdf")]
    [InlineData("a.docx")]
    [InlineData("a.xlsx")]
    [InlineData("a.md")]
    [InlineData("a.txt")]
    [InlineData("data.json")]
    [InlineData("notes.csv")]
    [InlineData("script.js")]
    [InlineData("config.yaml")]
    [InlineData("noext")]
    public void Any_extension_passes(string filename) =>
        _validator.TestValidate(Upload(filename: filename, mime: "text/plain"))
            .ShouldNotHaveValidationErrorFor(u => u.Filename);

    // The decision: the upload name is metadata, not a path. Exotic names are PRESERVED.
    // (Path traversal is impossible by construction: the blob is stored under a fully
    // server-generated files/{doc-id} key, no extension, never this name.)
    [Theory]
    [InlineData("../etc/passwd.pdf")]
    [InlineData("foo/bar.pdf")]
    [InlineData("foo\\bar.pdf")]
    [InlineData("..weird...name.txt")]
    [InlineData("C:\\Windows\\system32\\evil.txt")]
    [InlineData("../etc/passwd")]
    public void Exotic_filenames_are_preserved(string filename) =>
        _validator.TestValidate(Upload(filename: filename, mime: "text/plain"))
            .ShouldNotHaveValidationErrorFor(u => u.Filename);

    [Fact]
    public void Bidi_and_control_char_filenames_are_preserved()
    {
        // RTL-override spoof and an embedded control char: preserved (display safety is the SPA's job).
        var rtlSpoof = "apple" + (char)0x202E + "txt.exe.pdf";
        var withControl = "note" + (char)0x0007 + "bell.md";

        _validator.TestValidate(Upload(filename: rtlSpoof, mime: "application/pdf"))
            .ShouldNotHaveValidationErrorFor(u => u.Filename);
        _validator.TestValidate(Upload(filename: withControl, mime: "text/plain"))
            .ShouldNotHaveValidationErrorFor(u => u.Filename);
    }

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

    [Theory]
    [InlineData("application/json")]
    [InlineData("text/csv")]
    [InlineData("application/x-msdownload")]
    [InlineData("application/octet-stream")]
    public void Any_content_type_passes(string mime) =>
        _validator.TestValidate(Upload(mime: mime))
            .ShouldNotHaveValidationErrorFor(u => u.Mime);

    [Fact]
    public void Missing_content_type_fails() =>
        _validator.TestValidate(Upload(mime: string.Empty))
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

    // A null SizeBytes is NOT a size-cap bypass: the validator can only gate what
    // the host measured up front. For streaming callers the cap is enforced
    // mid-stream by DocumentService's CountingStream (limit = UploadConstraints
    // .MaxSizeBytes), which throws the same upload.too_large ValidationException -
    // see CountingStreamTests and the streamed-oversize test in
    // Gert.Database.Sqlite.Tests/IngestionPipelineTests.
    [Fact]
    public void Unknown_size_is_accepted_for_streamed_uploads() =>
        _validator.TestValidate(Upload(size: null))
            .ShouldNotHaveValidationErrorFor(u => u.SizeBytes!.Value);
}
