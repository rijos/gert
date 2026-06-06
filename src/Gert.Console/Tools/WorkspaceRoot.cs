namespace Gert.Console.Tools;

/// <summary>
/// The local workspace the TUI's file tools operate in (U16) — the directory
/// <c>gert</c> was launched from. Every tool path resolves through
/// <see cref="ResolveSafe"/>, which confines access to this root the same way
/// <c>LocalObjectStore</c> guards its keys: full-path expansion, then an
/// ordinal prefix check. Symlinks inside the root that point outside it are a
/// documented non-goal (the TUI is the user's own shell-equivalent access).
/// </summary>
public sealed class WorkspaceRoot
{
    /// <summary>Capture <paramref name="root"/> (normalized, no trailing separator).</summary>
    public WorkspaceRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    /// <summary>The absolute workspace root path.</summary>
    public string Root { get; }

    /// <summary>
    /// Resolve <paramref name="path"/> (relative to the root, or absolute and
    /// already inside it) to an absolute path. Throws
    /// <see cref="ArgumentException"/> when the result escapes the workspace —
    /// the caller maps that to the tool's failure shape.
    /// </summary>
    public string ResolveSafe(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Root, path));

        if (!IsInside(full))
        {
            throw new ArgumentException($"path escapes the workspace: {path}");
        }

        return full;
    }

    /// <summary>
    /// The workspace-relative form of an absolute <paramref name="path"/>
    /// (for display: tool cards, the touched-files pane). The root itself
    /// renders as <c>"."</c>.
    /// </summary>
    public string ToRelative(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var relative = Path.GetRelativePath(Root, path);
        return relative.Length == 0 ? "." : relative;
    }

    private bool IsInside(string full)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(full);
        return string.Equals(trimmed, Root, StringComparison.Ordinal)
            || trimmed.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
