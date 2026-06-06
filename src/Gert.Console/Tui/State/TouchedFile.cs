namespace Gert.Console.Tui.State;

/// <summary>One applied edit in the workspace pane (latest per path wins the diff view).</summary>
public sealed record TouchedFile
{
    /// <summary>Workspace-relative path.</summary>
    public required string Path { get; init; }

    /// <summary>The applying capability (<c>write_file</c> / <c>edit_file</c>).</summary>
    public required string Kind { get; init; }

    /// <summary>The unified diff of the LAST applied edit to this path.</summary>
    public string? Diff { get; init; }

    /// <summary>How many edits have landed on this path this session.</summary>
    public int Edits { get; init; }
}
