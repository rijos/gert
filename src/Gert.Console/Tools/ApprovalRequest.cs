namespace Gert.Console.Tools;

/// <summary>
/// One gated tool action awaiting the user's verdict (U16): a file write/edit
/// carrying its unified diff, or a shell command. Built by the write tools,
/// shown by the TUI's ApprovalDialog, recorded by the workspace pane.
/// </summary>
public sealed record ApprovalRequest
{
    /// <summary>The gated capability: <c>write_file</c> | <c>edit_file</c> | <c>shell</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Workspace-relative path (display; <c>"."</c> for shell).</summary>
    public required string Path { get; init; }

    /// <summary>Current file content (null for a new file / shell).</summary>
    public string? OldText { get; init; }

    /// <summary>Proposed file content (null for shell).</summary>
    public string? NewText { get; init; }

    /// <summary>Precomputed unified diff for the dialog and the workspace pane.</summary>
    public string? UnifiedDiff { get; init; }

    /// <summary>The shell command line (shell only).</summary>
    public string? Command { get; init; }
}
