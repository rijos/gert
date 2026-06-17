using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Gert.Tools.Builtin;

/// <summary>
/// Reduces fetched HTML to LLM-friendly plain text (chat-and-tools.md section web
/// fetch). Safety shape: a linear single-pass scanner over the already
/// byte-capped body - no DOM, no recursion, no external parser, so malformed
/// markup degrades to text, never to an error or unbounded work. Non-content
/// subtrees (<c>script</c>/<c>style</c>/<c>head</c>/...) are dropped whole;
/// block boundaries become newlines, headings keep their level as <c>#</c>
/// prefixes, list items become bullets. Entities decode LAST, after every tag
/// is gone - decoded markup (<c>&amp;lt;script&amp;gt;</c>) is therefore
/// content, never re-parsed - and the output only ever re-enters the prompt
/// and the tool card as plain text.
/// </summary>
public static partial class HtmlTextExtractor
{
    /// <summary>Subtrees whose text is never page content - skipped whole.</summary>
    private static readonly HashSet<string> NonContentContainers = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "template", "svg", "head",
        "iframe", "object", "embed", "canvas", "audio", "video",
    };

    /// <summary>Tags whose boundary breaks the text flow.</summary>
    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "section", "article", "header", "footer", "main", "aside",
        "nav", "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol", "table", "thead",
        "tbody", "tr", "blockquote", "pre", "figure", "figcaption", "form",
        "fieldset", "hr", "details", "summary", "dl", "dt", "dd", "address",
    };

    /// <summary>
    /// Cheap content sniff: is this body worth running through
    /// <see cref="ToPlainText"/>? JSON, plain text and most non-HTML pass
    /// through untouched on a false.
    /// </summary>
    public static bool LooksLikeHtml(string content)
    {
        var i = 0;
        while (i < content.Length && char.IsWhiteSpace(content[i]))
        {
            i++;
        }

        if (i >= content.Length)
        {
            return false;
        }

        if (content[i] == '<')
        {
            var rest = content.AsSpan(i);
            if (rest.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase)
                || rest.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
                || rest.StartsWith("<head", StringComparison.OrdinalIgnoreCase)
                || rest.StartsWith("<body", StringComparison.OrdinalIgnoreCase)
                || rest.StartsWith("<!--", StringComparison.Ordinal))
            {
                return true;
            }
        }

        // Pages that open with stray text or server banners: sniff a bounded
        // window for the unambiguous markers only.
        var window = content.AsSpan(0, Math.Min(content.Length, 1024));
        return window.Contains("<html", StringComparison.OrdinalIgnoreCase)
               || window.Contains("<!doctype html", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reduce an HTML body to plain text (title first, when present).</summary>
    public static string ToPlainText(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        var title = ExtractTitle(html);
        var sb = new StringBuilder(Math.Min(html.Length, 64 * 1024));
        var n = html.Length;
        var i = 0;

        while (i < n)
        {
            var c = html[i];
            if (c != '<')
            {
                sb.Append(c);
                i++;
                continue;
            }

            // <!-- comments --> drop whole; unterminated drops the rest.
            if (i + 3 < n && html[i + 1] == '!' && html[i + 2] == '-' && html[i + 3] == '-')
            {
                var end = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = end < 0 ? n : end + 3;
                continue;
            }

            // <!doctype ...> / <![CDATA[...]]> / <?xml ...?> - declarations, not content.
            if (i + 1 < n && html[i + 1] is '!' or '?')
            {
                var declEnd = html.IndexOf('>', i + 1);
                i = declEnd < 0 ? n : declEnd + 1;
                continue;
            }

            // Tag name; a nameless '<' (math, smileys) is text, not markup.
            var j = i + 1;
            var closing = j < n && html[j] == '/';
            if (closing)
            {
                j++;
            }

            var nameStart = j;
            while (j < n && char.IsAsciiLetterOrDigit(html[j]))
            {
                j++;
            }

            var name = html[nameStart..j];
            if (name.Length == 0)
            {
                sb.Append('<');
                i++;
                continue;
            }

            // Advance past '>' respecting quoted attribute values (a '>' inside
            // href="..." must not end the tag); an unterminated tag eats the rest.
            var k = j;
            var quote = '\0';
            while (k < n)
            {
                var ch = html[k];
                if (quote != '\0')
                {
                    if (ch == quote)
                    {
                        quote = '\0';
                    }
                }
                else if (ch is '"' or '\'')
                {
                    quote = ch;
                }
                else if (ch == '>')
                {
                    break;
                }

                k++;
            }

            var selfClosed = k < n && k > i && html[k - 1] == '/';
            i = k < n ? k + 1 : n;

            // Non-content subtree: skip to its close tag, case-insensitive.
            if (!closing && !selfClosed && NonContentContainers.Contains(name))
            {
                var idx = html.IndexOf("</" + name, i, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    break;
                }

                var gt = html.IndexOf('>', idx);
                i = gt < 0 ? n : gt + 1;
                continue;
            }

            // Structure -> text: headings keep their level, list items bullet,
            // any block boundary breaks the line, cells separate with a space.
            if (name.Equals("br", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append('\n');
            }
            else if (!closing && name.Equals("li", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append("\n- ");
            }
            else if (!closing && name.Length == 2
                     && (name[0] is 'h' or 'H') && name[1] is >= '1' and <= '6')
            {
                sb.Append("\n\n").Append('#', name[1] - '0').Append(' ');
            }
            else if (BlockTags.Contains(name))
            {
                sb.Append('\n');
            }
            else if (closing && (name.Equals("td", StringComparison.OrdinalIgnoreCase)
                                 || name.Equals("th", StringComparison.OrdinalIgnoreCase)))
            {
                sb.Append(' ');
            }
        }

        // Decode entities only now - whatever they decode to is content.
        var text = Collapse(WebUtility.HtmlDecode(sb.ToString()));
        return string.IsNullOrEmpty(title) || text.StartsWith(title, StringComparison.Ordinal)
            ? text
            : title + "\n\n" + text;
    }

    /// <summary>The page title, decoded and single-lined - or null.</summary>
    private static string? ExtractTitle(string html)
    {
        var open = html.IndexOf("<title", StringComparison.OrdinalIgnoreCase);
        if (open < 0)
        {
            return null;
        }

        var gt = html.IndexOf('>', open);
        if (gt < 0)
        {
            return null;
        }

        var close = html.IndexOf("</title", gt + 1, StringComparison.OrdinalIgnoreCase);
        if (close < 0)
        {
            return null;
        }

        var title = Collapse(WebUtility.HtmlDecode(html[(gt + 1)..close]));
        if (title.Length == 0)
        {
            return null;
        }

        // a title is one line of display metadata, never a transcript
        return title.Length > 300 ? title[..300] : title;
    }

    private static string Collapse(string text)
    {
        text = SpaceRuns().Replace(text, " ");
        text = NewlinePadding().Replace(text, "\n");
        text = NewlineRuns().Replace(text, "\n\n");
        return text.Trim();
    }

    [GeneratedRegex(@"[ \t\r\f\u00A0]+")]
    private static partial Regex SpaceRuns();

    [GeneratedRegex(@" ?\n ?")]
    private static partial Regex NewlinePadding();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex NewlineRuns();
}
