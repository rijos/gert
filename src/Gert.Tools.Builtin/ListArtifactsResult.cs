namespace Gert.Tools.Builtin;

/// <summary>
/// The model-facing payload of <c>list_artifacts</c>: every canvas artifact in the
/// conversation as a <see cref="ListedArtifact"/> (name, format, version). Read-only.
/// </summary>
public sealed record ListArtifactsResult
{
    public required IReadOnlyList<ListedArtifact> Artifacts { get; init; }
}
