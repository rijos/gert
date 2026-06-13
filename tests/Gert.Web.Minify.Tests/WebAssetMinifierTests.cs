using FluentAssertions;
using Gert.Web.Minify;
using Xunit;

namespace Gert.Web.Minify.Tests;

/// <summary>
/// Verifies the ESM-safe minifier (ui-components.md section 6): JS modules shrink while keeping
/// <c>import</c>/<c>export</c> resolvable, CSS shrinks, and a malformed file falls back to
/// raw so the import graph never breaks.
/// </summary>
public sealed class WebAssetMinifierTests
{
    private const string EsmSource = """
        import van from "../lib/van.js";
        export const Greeting = (name = "world") => {
            const message = `hello ${name}`;
            const upper = name?.toUpperCase();
            return van.tags.div({ class: "greeting" }, message, upper);
        };
        export const ANSWER = 42;
        """;

    [Fact]
    public void Minify_js_shrinks_and_keeps_import_and_export()
    {
        var (content, outcome) = WebAssetMinifier.Minify(EsmSource, ".js");

        outcome.Should().Be(MinifyOutcome.Minified);
        content.Should().NotBeNullOrEmpty();
        content.Length.Should().BeLessThan(EsmSource.Length);

        // The ESM seams must survive - other modules import these by name.
        content.Should().Contain("import");
        content.Should().Contain("export");
        content.Should().Contain("Greeting");
        content.Should().Contain("ANSWER");
    }

    [Fact]
    public void Minify_js_handles_namespace_imports()
    {
        // The house import style (`import * as chat from ...`) - NUglify < 1.21.17
        // failed to parse it (JS1332) and silently left every component raw, so
        // the published SPA shipped unminified. Pin the behavior.
        const string source = """
            import * as chat from "../../state/chat.js";
            import van from "van";

            // a comment that should vanish in the minified output
            export const titled = () => chat.title.val + "!";
            """;

        var (content, outcome) = WebAssetMinifier.Minify(source, ".js");

        outcome.Should().Be(MinifyOutcome.Minified);
        // whitespace compresses ("import*as chat") - assert the seam, not spacing
        content.Should().MatchRegex(@"import\s*\*\s*as chat");
        content.Should().NotContain("a comment that should vanish");
    }

    [Fact]
    public void Minify_reports_why_a_file_fell_back()
    {
        var (_, outcome, reason) = WebAssetMinifier.MinifyWithReason("this is { not js", ".js");

        outcome.Should().Be(MinifyOutcome.LeftRaw);
        reason.Should().NotBeNullOrEmpty("a silent fallback hides systematic failures");
    }

    [Fact]
    public void Minify_css_shrinks()
    {
        const string css = """
            .greeting {
                color: var(--fg);
                /* a comment that should vanish */
                padding: 8px   16px;
            }
            """;

        var (content, outcome) = WebAssetMinifier.Minify(css, ".css");

        outcome.Should().Be(MinifyOutcome.Minified);
        content.Length.Should().BeLessThan(css.Length);
        content.Should().Contain(".greeting");
        content.Should().NotContain("a comment that should vanish");
    }

    [Fact]
    public void Malformed_js_is_left_raw()
    {
        // Deliberately broken syntax - NUglify must report errors and we keep the source.
        const string broken = "export const = (((  function {{{ <<< not js at all";

        var (content, outcome) = WebAssetMinifier.Minify(broken, ".js");

        outcome.Should().Be(MinifyOutcome.LeftRaw);
        content.Should().Be(broken);
    }

    [Fact]
    public void MinifyFileInPlace_overwrites_only_minified_files()
    {
        var dir = Directory.CreateTempSubdirectory("gert-minify-");
        try
        {
            var js = Path.Combine(dir.FullName, "app.js");
            var bad = Path.Combine(dir.FullName, "broken.js");
            File.WriteAllText(js, EsmSource);
            File.WriteAllText(bad, "export const = (((  not js");

            var jsResult = WebAssetMinifier.MinifyFileInPlace(js);
            var badResult = WebAssetMinifier.MinifyFileInPlace(bad);

            jsResult.Outcome.Should().Be(MinifyOutcome.Minified);
            jsResult.BytesSaved.Should().BeGreaterThan(0);
            File.ReadAllText(js).Length.Should().BeLessThan(EsmSource.Length);

            // Fallback: the malformed file is byte-for-byte untouched.
            badResult.Outcome.Should().Be(MinifyOutcome.LeftRaw);
            File.ReadAllText(bad).Should().Be("export const = (((  not js");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Runner_walks_directory_and_reports_summary()
    {
        var dir = Directory.CreateTempSubdirectory("gert-minify-run-");
        try
        {
            var lib = Directory.CreateDirectory(Path.Combine(dir.FullName, "lib"));
            File.WriteAllText(Path.Combine(dir.FullName, "app.js"), EsmSource);
            File.WriteAllText(Path.Combine(lib.FullName, "broken.js"), "export const = (((");
            File.WriteAllText(Path.Combine(dir.FullName, "styles.css"), ".a{color:red;  }\n.b{ }");
            File.WriteAllText(Path.Combine(dir.FullName, "ignore.txt"), "not an asset");

            using var log = new StringWriter();
            var summary = new MinifyRunner(log).Run(dir.FullName);

            summary.Total.Should().Be(3); // 2 js + 1 css; the .txt is ignored.
            summary.Minified.Should().BeGreaterThanOrEqualTo(1);
            summary.LeftRaw.Should().BeGreaterThanOrEqualTo(1);
            summary.BytesSaved.Should().BeGreaterThan(0);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
