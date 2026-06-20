namespace Gert.Tools.Builtin;

/// <summary>
/// The model-facing payload of <c>make_artifact</c>: the artifact name, its
/// resolved canonical <see cref="Format"/> word, the <see cref="Action"/>
/// (<c>created</c>/<c>updated</c>), and the new version. The artifact itself rides
/// the <c>Artifacts</c> side-channel.
/// </summary>
public sealed record MakeArtifactResult
{
    public required string Name { get; init; }

    public required string Format { get; init; }

    public required string Action { get; init; }

    public required int Version { get; init; }
}
