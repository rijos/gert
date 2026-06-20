namespace Gert.Tools.Results;

/// <summary>
/// The model-facing payload of <c>edit_artifact</c>: the artifact name, the
/// <see cref="Action"/> (always <c>edited</c>), and the new version. No format word
/// (an edit never changes the kind); the updated artifact rides the
/// <c>Artifacts</c> side-channel.
/// </summary>
public sealed record EditArtifactResult
{
    public required string Name { get; init; }

    public required string Action { get; init; }

    public required int Version { get; init; }
}
