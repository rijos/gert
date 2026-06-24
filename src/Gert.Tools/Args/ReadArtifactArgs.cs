using Gert.Tools.Schema;

namespace Gert.Tools.Args;

/// <summary>
/// Arguments for the canvas read tool (<c>read_artifact</c>): the artifact
/// <see cref="Name"/> and an optional 1-indexed <see cref="Range"/> of exactly two
/// line numbers [start, end] (end -1 reads to the end). A supplied range must
/// carry exactly two integers; the tool resolves the window.
/// </summary>
public sealed record ReadArtifactArgs
{
    /// <summary>Name of the artifact to read (required).</summary>
    [ToolParameterDescription("Name of the artifact to read.")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Optional [start, end] line numbers, 1-indexed; exactly two when present.</summary>
    [ToolParameterDescription("Optional [start, end] line numbers, 1-indexed; end -1 reads to the end.")]
    [ToolParameterItems(2, 2)]
    public IReadOnlyList<int>? Range { get; init; }
}
