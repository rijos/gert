using System.Text;
using FluentAssertions;
using Gert.Ingestion.PlainText;
using Xunit;

namespace Gert.Ingestion.Tests;

/// <summary>
/// Unit tests for the universal <see cref="PlainTextExtractor"/>: it handles every type except the
/// binary document formats (pdf/docx/xlsx, which go to the isolated extractor), decodes UTF-8, and
/// fails non-text bytes ("not a text file") so a binary upload with an innocent name never yields
/// usable text.
/// </summary>
public sealed class PlainTextExtractorTests
{
    private readonly PlainTextExtractor _extractor = new();

    [Theory]
    [InlineData("txt", true)]
    [InlineData("md", true)]
    [InlineData("json", true)]
    [InlineData("csv", true)]
    [InlineData("yaml", true)]
    [InlineData("log", true)]
    [InlineData("", true)] // no-extension files are candidate text
    [InlineData("pdf", false)]
    [InlineData("docx", false)]
    [InlineData("xlsx", false)]
    public void CanExtract_handles_everything_but_binary_document_formats(string ext, bool expected) =>
        _extractor.CanExtract(ext).Should().Be(expected);

    [Fact]
    public async Task ExtractAsync_returns_the_text_of_a_json_file()
    {
        const string json = "{\n  \"name\": \"gert\",\n  \"ok\": true\n}";
        using var content = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var result = await _extractor.ExtractAsync(content, "json");

        result.Error.Should().BeNull();
        result.HasText.Should().BeTrue();
        result.Pages.Single().Text.Should().Be(json);
    }

    [Fact]
    public async Task ExtractAsync_honours_a_utf8_bom()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("hello")).ToArray();
        using var content = new MemoryStream(bytes);

        var result = await _extractor.ExtractAsync(content, "txt");

        result.Pages.Single().Text.Should().Be("hello");
    }

    [Fact]
    public async Task ExtractAsync_fails_binary_content_with_a_nul_byte()
    {
        using var content = new MemoryStream([0x50, 0x4B, 0x03, 0x04, 0x00, 0x01]); // zip-ish, has NUL

        var result = await _extractor.ExtractAsync(content, "json");

        result.Error.Should().Be("not a text file");
        result.Pages.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_whitespace_only_yields_no_usable_text()
    {
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("   \n\t  "));

        var result = await _extractor.ExtractAsync(content, "txt");

        // Decodes fine, but there is no usable text -> the pipeline marks the doc failed.
        result.HasText.Should().BeFalse();
    }

    [Fact]
    public async Task ExtractAsync_refuses_a_binary_document_format()
    {
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("not really a pdf"));

        var result = await _extractor.ExtractAsync(content, "pdf");

        result.Error.Should().NotBeNull();
        result.Pages.Should().BeEmpty();
    }
}
