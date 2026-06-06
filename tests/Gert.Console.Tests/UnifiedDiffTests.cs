using FluentAssertions;
using Gert.Console.Tools;
using Xunit;

namespace Gert.Console.Tests;

/// <summary>
/// The in-repo unified-diff producer behind the TUI's edit-approval flow
/// (U16). These diffs are for human review (ApprovalDialog / DiffView) and
/// for the model on denial — correctness of +/-/context lines matters, byte
/// parity with GNU diff does not.
/// </summary>
public sealed class UnifiedDiffTests
{
    [Fact]
    public void Identical_content_yields_an_empty_diff()
    {
        UnifiedDiff.Compute("a\nb\n", "a\nb\n", "f.txt").Should().BeEmpty();
    }

    [Fact]
    public void New_file_shows_every_line_as_added()
    {
        var diff = UnifiedDiff.Compute(null, "one\ntwo", "new.txt");

        diff.Should().StartWith("--- a/new.txt\n+++ b/new.txt\n");
        diff.Should().Contain("+one\n").And.Contain("+two\n");
        diff.Should().NotContain("\n-");
    }

    [Fact]
    public void Changed_line_shows_minus_then_plus_with_context()
    {
        var oldText = "a\nb\nc\nd\ne";
        var newText = "a\nb\nX\nd\ne";

        var diff = UnifiedDiff.Compute(oldText, newText, "f.txt");

        diff.Should().Contain("-c\n").And.Contain("+X\n");
        diff.Should().Contain(" b\n").And.Contain(" d\n");
        diff.Should().Contain("@@ -1,5 +1,5 @@");
    }

    [Fact]
    public void Pure_insertion_keeps_surrounding_lines_as_context()
    {
        var diff = UnifiedDiff.Compute("a\nb", "a\nNEW\nb", "f.txt");

        diff.Should().Contain("+NEW\n");
        diff.Should().Contain(" a\n").And.Contain(" b\n");
        diff.Should().NotContain("\n-");
    }

    [Fact]
    public void Pure_deletion_marks_removed_lines()
    {
        var diff = UnifiedDiff.Compute("a\ngone\nb", "a\nb", "f.txt");

        diff.Should().Contain("-gone\n");
        diff.Split('\n').Skip(2).Should().NotContain(l => l.StartsWith('+'), "a pure deletion adds nothing");
    }

    [Fact]
    public void Distant_changes_produce_separate_hunks()
    {
        var oldLines = Enumerable.Range(1, 30).Select(i => $"line{i}");
        var oldText = string.Join('\n', oldLines);
        var newText = oldText.Replace("line2", "LINE2", StringComparison.Ordinal)
            .Replace("line28", "LINE28", StringComparison.Ordinal);

        var diff = UnifiedDiff.Compute(oldText, newText, "f.txt");

        diff.Split("@@ -").Length.Should().Be(3, "two changes 26 lines apart must not share a hunk");
        diff.Should().Contain("-line2\n").And.Contain("+LINE2\n");
        diff.Should().Contain("-line28\n").And.Contain("+LINE28\n");
        diff.Should().NotContain(" line15\n", "far-from-change lines are not context");
    }

    [Fact]
    public void Adjacent_changes_share_one_hunk()
    {
        var oldText = "a\nb\nc\nd\ne\nf\ng";
        var newText = "a\nB\nc\nd\nE\nf\ng";

        var diff = UnifiedDiff.Compute(oldText, newText, "f.txt");

        diff.Split("@@ -").Length.Should().Be(2, "changes 2 lines apart share context");
    }

    [Fact]
    public void Oversized_input_falls_back_to_a_replace_hunk()
    {
        var oldText = string.Join('\n', Enumerable.Range(0, 4000).Select(i => $"o{i}"));
        var newText = string.Join('\n', Enumerable.Range(0, 4000).Select(i => $"n{i}"));

        var diff = UnifiedDiff.Compute(oldText, newText, "big.txt");

        diff.Should().Contain("-o0\n").And.Contain("+n0\n");
        diff.Should().Contain("-o3999\n").And.Contain("+n3999\n");
    }

    [Fact]
    public void Trailing_newline_difference_is_visible()
    {
        var diff = UnifiedDiff.Compute("a\nb", "a\nb\n", "f.txt");

        diff.Should().NotBeEmpty("adding a trailing newline is a real change");
    }
}
