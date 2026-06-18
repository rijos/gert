using FluentAssertions;
using Gert.Web.Bundle;
using Xunit;

namespace Gert.Web.Bundle.Tests;

/// <summary>
/// On publish the bundler repoints index.html from the four global sheets to the single
/// bundled /app.css, while leaving the /app.js entry tag alone (it keeps a stable filename).
/// </summary>
public sealed class IndexHtmlRewriteTests
{
    private const string Head =
        """
        <!doctype html>
        <html>
          <head>
            <link rel="icon" href="/favicon.svg" type="image/svg+xml" />
            <link rel="stylesheet" href="/styles/tokens.css" />
            <link rel="stylesheet" href="/styles/base.css" />
            <link rel="stylesheet" href="/styles/layout.css" />
            <link rel="stylesheet" href="/styles/primitives.css" />
          </head>
          <body>
            <script type="module" src="/app.js"></script>
          </body>
        </html>
        """;

    [Fact]
    public void Folds_the_global_sheets_into_one_app_css_link()
    {
        var result = Bundler.RewriteIndexHtml(Head);

        result.Should().Contain("""<link rel="stylesheet" href="/app.css" />""");
        result.Should().NotContain("/styles/");
        // Exactly one stylesheet link remains (favicon link is rel="icon", untouched).
        CountOccurrences(result, "rel=\"stylesheet\"").Should().Be(1);
    }

    [Fact]
    public void Leaves_the_app_js_entry_and_favicon_untouched()
    {
        var result = Bundler.RewriteIndexHtml(Head);

        result.Should().Contain("""<script type="module" src="/app.js"></script>""");
        result.Should().Contain("""<link rel="icon" href="/favicon.svg" type="image/svg+xml" />""");
    }

    [Fact]
    public void Keeps_the_first_links_indentation()
    {
        var result = Bundler.RewriteIndexHtml(Head);

        result.Should().Contain("""    <link rel="stylesheet" href="/app.css" />""");
    }

    [Fact]
    public void Throws_when_there_is_nothing_to_repoint()
    {
        const string noStyles = "<html><head></head><body></body></html>";

        var act = () => Bundler.RewriteIndexHtml(noStyles);

        act.Should().Throw<InvalidOperationException>();
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
