namespace Gert.Console.Tui.State;

/// <summary>
/// The TUI's light markdown line-classifier (U16): fenced code blocks and
/// headings get their own <see cref="LineKind"/>; everything else stays plain
/// text. Deliberately NOT a parser — the web's sanitizer/renderer problems
/// don't exist on a character terminal, so line-level styling is enough.
/// </summary>
public static class MarkdownLite
{
    /// <summary>Classify <paramref name="text"/> into styled transcript lines.</summary>
    public static IEnumerable<(string Text, LineKind Kind)> Classify(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var inFence = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                yield return (line, LineKind.Code);
                continue;
            }

            if (inFence)
            {
                yield return (line, LineKind.Code);
            }
            else if (line.StartsWith('#'))
            {
                yield return (line, LineKind.Heading);
            }
            else
            {
                yield return (line, LineKind.Body);
            }
        }
    }
}
