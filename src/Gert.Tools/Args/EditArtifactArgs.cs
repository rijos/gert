using Gert.Tools.Schema;

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
    [ToolParameterDescription("Name of the artifact to edit.")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Exact text to find - must match one location verbatim (required).</summary>
    [ToolParameterDescription("Exact text to find - must match a single location verbatim.")]
    public string OldStr { get; init; } = string.Empty;

    /// <summary>Replacement text; empty deletes the snippet. Non-nullable so it is required in the schema.</summary>
    [ToolParameterDescription("Replacement text (may be empty to delete the snippet).")]
    public string NewStr { get; init; } = string.Empty;
}
