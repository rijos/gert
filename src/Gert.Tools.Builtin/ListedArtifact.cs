namespace Gert.Tools.Builtin;

/// <summary>
/// One row of <c>list_artifacts</c>: an artifact's name, its canonical format word,
/// and current version - the handles the model needs to pick a file to read or edit.
/// </summary>
public sealed record ListedArtifact
{
    public required string Name { get; init; }

    public required string Format { get; init; }

    public required int Version { get; init; }
}
