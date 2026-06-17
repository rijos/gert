using FluentAssertions;
using Gert.Tools.Builtin;
using Xunit;

namespace Gert.Tools.Tests;

/// <summary>
/// Unit tests for <see cref="HtmlTextExtractor"/> - the web_fetch HTML->text
/// reduction. The safety contract under test: non-content subtrees vanish
/// whole, entities decode only after every tag is gone (decoded markup is
/// content, never re-parsed), and malformed markup degrades to text instead
/// of throwing.
/// </summary>
public sealed class HtmlTextExtractorTests
{
    [Fact]
    public void Strips_scripts_styles_and_head_keeps_body_text_with_title_first()
    {
        const string html = """
            <!doctype html>
            <html><head>
              <title>Release notes</title>
              <style>body { color: red; }</style>
              <script>tracker.init();</script>
            </head>
            <body>
              <h1>What changed</h1>
              <p>The fetcher now extracts text.</p>
              <script>more.tracking();</script>
            </body></html>
            """;

        var text = HtmlTextExtractor.ToPlainText(html);

        text.Should().StartWith("Release notes");
        text.Should().Contain("# What changed");
        text.Should().Contain("The fetcher now extracts text.");
        text.Should().NotContain("tracker.init");
        text.Should().NotContain("color: red");
        text.Should().NotContain("<");
    }

    [Fact]
    public void Headings_keep_their_level_and_list_items_become_bullets()
    {
        var text = HtmlTextExtractor.ToPlainText(
            "<body><h2>Setup</h2><ul><li>clone</li><li>build</li></ul></body>");

        text.Should().Contain("## Setup");
        text.Should().Contain("- clone");
        text.Should().Contain("- build");
    }

    [Fact]
    public void Entities_decode_after_stripping_so_encoded_markup_stays_text()
    {
        var text = HtmlTextExtractor.ToPlainText(
            "<p>Use &lt;script&gt; tags sparingly &amp; wisely.</p>");

        // The decoded "<script>" is content in the OUTPUT - it was never
        // parsed as markup (a real <script> subtree is dropped whole instead).
        text.Should().Be("Use <script> tags sparingly & wisely.");
    }

    [Fact]
    public void A_gt_inside_a_quoted_attribute_does_not_end_the_tag()
    {
        var text = HtmlTextExtractor.ToPlainText(
            """<p><a href="https://example.test/?q=a>b" title='2>1'>the link</a> works</p>""");

        text.Should().Be("the link works");
    }

    [Fact]
    public void Malformed_markup_degrades_to_text_and_never_throws()
    {
        HtmlTextExtractor.ToPlainText("<html><p>unterminated")
            .Should().Contain("unterminated");
        HtmlTextExtractor.ToPlainText("<p>stray < bracket and 1 < 2 math</p>")
            .Should().Contain("1 < 2");
        HtmlTextExtractor.ToPlainText("<script>never closed")
            .Should().BeEmpty();
        HtmlTextExtractor.ToPlainText("<p attr=\"never closed")
            .Should().BeEmpty();
    }

    [Fact]
    public void Whitespace_collapses_to_readable_paragraphs()
    {
        var text = HtmlTextExtractor.ToPlainText(
            "<body><div>  one   line </div><div></div><div></div><div>next</div></body>");

        // runs of empty blocks collapse to at most ONE blank line
        text.Should().Be("one line\n\nnext");
    }

    [Theory]
    [InlineData("<!doctype html><html><body>x</body></html>", true)]
    [InlineData("  <html lang=\"en\"><body>x</body></html>", true)]
    [InlineData("banner text first <html><body>x</body>", true)]
    [InlineData("{\"json\": true}", false)]
    [InlineData("plain text, nothing else", false)]
    [InlineData("", false)]
    public void LooksLikeHtml_sniffs_documents_not_data(string content, bool expected) =>
        HtmlTextExtractor.LooksLikeHtml(content).Should().Be(expected);
}
