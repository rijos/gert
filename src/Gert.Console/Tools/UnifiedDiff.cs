using System.Text;

namespace Gert.Console.Tools;

/// <summary>
/// A small line-based unified-diff producer for the TUI's edit-approval flow
/// (U16): the write tools diff old vs new content, the
/// <c>ApprovalDialog</c>/<c>DiffView</c> render it, and a denied edit returns
/// it to the model. LCS on lines with common prefix/suffix trimming; inputs
/// beyond <see cref="MaxLcsLines"/>² collapse to a single replace hunk rather
/// than risking quadratic blow-up (these diffs are for human review, not
/// patch(1) round-trips).
/// </summary>
public static class UnifiedDiff
{
    private const int ContextLines = 3;
    private const int MaxLcsLines = 3000;

    /// <summary>
    /// Compute a unified diff between <paramref name="oldText"/> (null = new
    /// file) and <paramref name="newText"/>, labelled with
    /// <paramref name="path"/>. Returns an empty string when the contents are
    /// identical.
    /// </summary>
    public static string Compute(string? oldText, string newText, string path)
    {
        ArgumentNullException.ThrowIfNull(newText);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (string.Equals(oldText, newText, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var oldLines = SplitLines(oldText ?? string.Empty);
        var newLines = SplitLines(newText);
        if (oldText is null or "")
        {
            oldLines = [];
        }

        var ops = Diff(oldLines, newLines);
        var sb = new StringBuilder();
        sb.Append("--- a/").Append(path).Append('\n');
        sb.Append("+++ b/").Append(path).Append('\n');
        AppendHunks(sb, ops, oldLines, newLines);
        return sb.ToString();
    }

    private static string[] SplitLines(string text) =>
        text.Length == 0 ? [string.Empty] : text.Split('\n');

    /// <summary>One diff opcode: how many lines match, are deleted, are inserted.</summary>
    private readonly record struct Op(int Match, int Delete, int Insert);

    /// <summary>
    /// Produce edit ops via prefix/suffix trim + LCS over the middle. Falls
    /// back to a whole-middle replace when the middle exceeds the LCS cap.
    /// </summary>
    private static List<Op> Diff(string[] oldLines, string[] newLines)
    {
        var prefix = 0;
        while (prefix < oldLines.Length
            && prefix < newLines.Length
            && string.Equals(oldLines[prefix], newLines[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < oldLines.Length - prefix
            && suffix < newLines.Length - prefix
            && string.Equals(
                oldLines[oldLines.Length - 1 - suffix],
                newLines[newLines.Length - 1 - suffix],
                StringComparison.Ordinal))
        {
            suffix++;
        }

        var oldMid = oldLines.Length - prefix - suffix;
        var newMid = newLines.Length - prefix - suffix;

        var ops = new List<Op>();
        if (prefix > 0)
        {
            ops.Add(new Op(prefix, 0, 0));
        }

        if (oldMid > MaxLcsLines || newMid > MaxLcsLines)
        {
            ops.Add(new Op(0, oldMid, newMid));
        }
        else if (oldMid > 0 || newMid > 0)
        {
            ops.AddRange(LcsOps(oldLines, prefix, oldMid, newLines, prefix, newMid));
        }

        if (suffix > 0)
        {
            ops.Add(new Op(suffix, 0, 0));
        }

        return ops;
    }

    private static List<Op> LcsOps(
        string[] oldLines,
        int oldStart,
        int oldCount,
        string[] newLines,
        int newStart,
        int newCount)
    {
        // Classic LCS length table over the (trimmed) middle window.
        var table = new int[oldCount + 1, newCount + 1];
        for (var i = oldCount - 1; i >= 0; i--)
        {
            for (var j = newCount - 1; j >= 0; j--)
            {
                table[i, j] = string.Equals(
                        oldLines[oldStart + i], newLines[newStart + j], StringComparison.Ordinal)
                    ? table[i + 1, j + 1] + 1
                    : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }

        // Walk the table one line at a time, then coalesce runs into ops.
        var ops = new List<Op>();
        int x = 0, y = 0;
        int match = 0, del = 0, ins = 0;
        while (x < oldCount || y < newCount)
        {
            if (x < oldCount
                && y < newCount
                && string.Equals(oldLines[oldStart + x], newLines[newStart + y], StringComparison.Ordinal))
            {
                if (del > 0 || ins > 0)
                {
                    ops.Add(new Op(0, del, ins));
                    del = 0;
                    ins = 0;
                }

                match++;
                x++;
                y++;
            }
            else
            {
                if (match > 0)
                {
                    ops.Add(new Op(match, 0, 0));
                    match = 0;
                }

                if (y >= newCount || (x < oldCount && table[x + 1, y] >= table[x, y + 1]))
                {
                    del++;
                    x++;
                }
                else
                {
                    ins++;
                    y++;
                }
            }
        }

        if (match > 0)
        {
            ops.Add(new Op(match, 0, 0));
        }

        if (del > 0 || ins > 0)
        {
            ops.Add(new Op(0, del, ins));
        }

        return ops;
    }

    private static void AppendHunks(StringBuilder sb, List<Op> ops, string[] oldLines, string[] newLines)
    {
        int oi = 0, ni = 0;
        var index = 0;
        while (index < ops.Count)
        {
            // Skip pure-match ops until the next change.
            while (index < ops.Count && ops[index] is { Delete: 0, Insert: 0 })
            {
                oi += ops[index].Match;
                ni += ops[index].Match;
                index++;
            }

            if (index >= ops.Count)
            {
                break;
            }

            // Hunk: pull back up to ContextLines of leading context.
            var lead = Math.Min(ContextLines, Math.Min(oi, ni));
            var hunkOldStart = oi - lead;
            var hunkNewStart = ni - lead;
            var body = new List<string>();
            for (var k = 0; k < lead; k++)
            {
                body.Add(" " + oldLines[hunkOldStart + k]);
            }

            // Consume ops until a match run longer than 2×context (hunk break).
            while (index < ops.Count)
            {
                var op = ops[index];
                if (op is { Delete: 0, Insert: 0 })
                {
                    var isLast = index == ops.Count - 1;
                    if (isLast || op.Match > ContextLines * 2)
                    {
                        // Trailing context, then close the hunk.
                        var trail = Math.Min(ContextLines, op.Match);
                        for (var k = 0; k < trail; k++)
                        {
                            body.Add(" " + oldLines[oi + k]);
                        }

                        oi += op.Match;
                        ni += op.Match;
                        index++;
                        break;
                    }

                    for (var k = 0; k < op.Match; k++)
                    {
                        body.Add(" " + oldLines[oi + k]);
                    }

                    oi += op.Match;
                    ni += op.Match;
                    index++;
                    continue;
                }

                for (var k = 0; k < op.Delete; k++)
                {
                    body.Add("-" + oldLines[oi + k]);
                }

                for (var k = 0; k < op.Insert; k++)
                {
                    body.Add("+" + newLines[ni + k]);
                }

                oi += op.Delete;
                ni += op.Insert;
                index++;
            }

            var oldCount = body.Count(l => l[0] is ' ' or '-');
            var newCount = body.Count(l => l[0] is ' ' or '+');
            sb.Append("@@ -")
                .Append(oldCount == 0 ? hunkOldStart : hunkOldStart + 1)
                .Append(',')
                .Append(oldCount)
                .Append(" +")
                .Append(newCount == 0 ? hunkNewStart : hunkNewStart + 1)
                .Append(',')
                .Append(newCount)
                .Append(" @@\n");
            foreach (var line in body)
            {
                sb.Append(line).Append('\n');
            }
        }
    }
}
