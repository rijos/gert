using FluentAssertions;
using Gert.Model;
using Gert.Service.Chat;
using Xunit;

namespace Gert.Service.Tests;

/// <summary>
/// The artifact-extraction contract (implementation-plan U7b): only a fenced
/// block that NAMES itself (<c>```html name=demo.html</c>) becomes a canvas
/// artifact; plain code blocks and unknown languages stay inline. Pure function
/// — the same content always yields the same artifacts, which is what the
/// fixture-driven E2E relies on.
/// </summary>
public sealed class ArtifactExtractorTests
{
    [Fact]
    public void Named_html_fence_becomes_an_html_artifact()
    {
        var content =
            "Here you go:\n\n```html name=demo.html\n<h1>Demo</h1>\n<p>hi</p>\n```\n\nOpened in the canvas.";

        var artifacts = ArtifactExtractor.Extract(content);

        var artifact = artifacts.Should().ContainSingle().Subject;
        artifact.Kind.Should().Be(ArtifactKind.Html);
        artifact.Name.Should().Be("demo.html");
        artifact.Language.Should().Be("html");
        artifact.Content.Should().Be("<h1>Demo</h1>\n<p>hi</p>");
    }

    [Theory]
    [InlineData("md", ArtifactKind.Md)]
    [InlineData("markdown", ArtifactKind.Md)]
    [InlineData("htm", ArtifactKind.Html)]
    [InlineData("svg", ArtifactKind.Svg)]
    [InlineData("py", ArtifactKind.Py)]
    [InlineData("python", ArtifactKind.Py)]
    public void Language_token_maps_onto_the_closed_kind_set(string lang, ArtifactKind expected)
    {
        var artifacts = ArtifactExtractor.Extract($"```{lang} name=a.out\nbody\n```");

        artifacts.Should().ContainSingle().Which.Kind.Should().Be(expected);
    }

    [Fact]
    public void Unnamed_fences_and_unknown_languages_stay_inline()
    {
        var content =
            "```python\nprint(1)\n```\n\n" + // no name= → inline code block
            "```rust name=main.rs\nfn main() {}\n```"; // unknown kind → inline

        ArtifactExtractor.Extract(content).Should().BeEmpty();
    }

    [Fact]
    public void Multiple_named_fences_extract_in_order()
    {
        var content =
            "```md name=notes.md\n# Notes\n```\n\ntext between\n\n```svg name=logo.svg\n<svg/>\n```";

        var artifacts = ArtifactExtractor.Extract(content);

        artifacts.Should().HaveCount(2);
        artifacts[0].Name.Should().Be("notes.md");
        artifacts[1].Name.Should().Be("logo.svg");
    }

    [Fact]
    public void A_reused_name_collapses_to_the_last_occurrence()
    {
        var content =
            "```md name=plan.md\ndraft\n```\n\n" +
            "```md name=plan.md\nfinal\n```";

        var artifacts = ArtifactExtractor.Extract(content);

        artifacts.Should().ContainSingle().Which.Content.Should().Be("final");
    }

    [Fact]
    public void Fence_at_end_of_content_without_trailing_newline_still_extracts()
    {
        var artifacts = ArtifactExtractor.Extract("```html name=x.html\n<b/>\n```");

        artifacts.Should().ContainSingle().Which.Content.Should().Be("<b/>");
    }

    [Fact]
    public void Empty_and_fence_free_content_extracts_nothing()
    {
        ArtifactExtractor.Extract(string.Empty).Should().BeEmpty();
        ArtifactExtractor.Extract("just prose, no fences").Should().BeEmpty();
    }
}
