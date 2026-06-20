namespace Gert.Tools.Args;

/// <summary>
/// Arguments for the canvas edit tool (<c>edit_artifact</c>): the artifact
/// <see cref="Name"/>, the exact <see cref="OldStr"/> to find (wire <c>old_str</c>,
/// required and matched verbatim once by the tool), and the <see cref="NewStr"/>
/// replacement (wire <c>new_str</c>, may be empty to delete the snippet).
/// </summary>
public sealed record EditArtifactArgs
{
    /// <summary>Name of the artifact to edit (required).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Exact text to find - must match one location verbatim (required).</summary>
    public string OldStr { get; init; } = string.Empty;

    /// <summary>Replacement text; null/empty deletes the snippet.</summary>
    public string? NewStr { get; init; }
}
