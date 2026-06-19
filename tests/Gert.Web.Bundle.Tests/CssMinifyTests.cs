using FluentAssertions;
using Gert.Web.Bundle;
using Xunit;

namespace Gert.Web.Bundle.Tests;

/// <summary>
/// esbuild's JS minifier never reaches inside a string/template literal, so the bundler minifies
/// the component <c>css:</c> blocks itself before esbuild inlines them. These pin the pure
/// transform (mirrors <c>lib/component.ts</c> minifyCss) - comments out, whitespace collapsed,
/// selectors and <c>content:""</c> values left intact.
/// </summary>
public sealed class CssMinifyTests
{
    [Fact]
    public void Strips_comments_including_legal_banners()
    {
        Bundler.MinifyCss("/* a */ .x{color:red} /*! keep? no */").Should().Be(".x{color:red}");
    }

    [Fact]
    public void Collapses_whitespace_and_newlines()
    {
        const string css =
            """
            .x {
              color: red;
              margin: 0;
            }
            """;

        Bundler.MinifyCss(css).Should().Be(".x{color: red;margin: 0}");
    }

    [Fact]
    public void Drops_the_redundant_trailing_semicolon()
    {
        Bundler.MinifyCss(".x{color:red;}").Should().Be(".x{color:red}");
    }

    [Fact]
    public void Preserves_descendant_combinator_spaces_and_content_values()
    {
        // The conservative minifier must NOT tighten around `:`/`,` - a descendant selector
        // (".a .b") and an empty content value would otherwise change meaning.
        Bundler.MinifyCss(".a .b{content: \"\";gap: 1px}").Should().Be(".a .b{content: \"\";gap: 1px}");
    }

    [Fact]
    public void Is_idempotent()
    {
        var once = Bundler.MinifyCss("/* c */ .x { color: red; }");
        Bundler.MinifyCss(once).Should().Be(once);
    }

    [Fact]
    public void MinifyInlineCssText_only_rewrites_css_template_bodies()
    {
        const string source =
            """
            export const C = component({
              name: "x",
              css: `
                .x {
                  color: red; /* note */
                }
              `,
              view: () => div({ class: "x" }, "css: not a block `keep me`"),
            });
            """;

        var result = Bundler.MinifyInlineCssText(source);

        result.Should().Contain("css: `.x{color: red}`");
        result.Should().NotContain("/* note */");
        // The string literal in `view` (it merely contains the text "css:") is untouched.
        result.Should().Contain("\"css: not a block `keep me`\"");
    }

    [Fact]
    public void MinifyInlineCssText_minifies_css_tagged_templates()
    {
        // toast (and any standalone adopted sheet) authors CSS via the `css` tag, not a `css:`
        // property - the bundler must minify those too.
        const string source = "const CSS = css`\n  .toast { color: red; }\n`;\n";

        Bundler.MinifyInlineCssText(source).Should().Be("const CSS = css`.toast{color: red}`;\n");
    }

    [Fact]
    public void MinifyInlineCssText_ignores_a_backtick_wrapped_css_token_in_a_comment()
    {
        // A doc comment may mention the `css` tag between backticks; that must not be treated as
        // the tag itself (regression for the comment-swallow bug).
        const string source =
            "// the `css` tag minifies inline\n"
            + "const CSS = css`\n  .x { color: red; }\n`;\n";

        var result = Bundler.MinifyInlineCssText(source);

        result.Should().Contain("// the `css` tag minifies inline"); // comment untouched
        result.Should().Contain("const CSS = css`.x{color: red}`;"); // real tag minified
    }

    [Fact]
    public void MinifyInlineCssText_does_not_match_an_identifier_ending_in_css()
    {
        // The `css` tag branch must not fire on e.g. `notcss` followed by a template.
        const string source = "const x = notcss`leave me`;\n";

        Bundler.MinifyInlineCssText(source).Should().Be(source);
    }

    [Fact]
    public void MinifyInlineCssText_ignores_css_tokens_outside_a_line_start_property()
    {
        // A `css:` inside a comment or string literal (never at line-start) must be left alone -
        // regression for the comment-swallow bug that ate the following export.
        const string source =
            "// a plain string `css:` works too\n"
            + "export const adoptStyles = (s) => s;\n"
            + "const C = component({\n"
            + "  css: `\n    .x { color: red; }\n  `,\n"
            + "});\n";

        var result = Bundler.MinifyInlineCssText(source);

        result.Should().Contain("export const adoptStyles = (s) => s;"); // not swallowed
        result.Should().Contain("`css:` works too"); // the comment token is untouched
        result.Should().Contain("css: `.x{color: red}`"); // the real property IS minified
    }

    [Fact]
    public void MinifyInlineCssText_leaves_a_source_with_no_css_blocks_unchanged()
    {
        const string source = "export const x = 1; // css? no template here\n";

        Bundler.MinifyInlineCssText(source).Should().Be(source);
    }
}
