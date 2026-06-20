namespace Gert.Tools.Results;

/// <summary>
/// The model-facing payload of <c>read_artifact</c>: the artifact name, its format
/// word, version, total <see cref="LineCount"/> (wire <c>line_count</c>), and the
/// line-numbered <see cref="Content"/> window. Read-only - no side-channels.
/// </summary>
public sealed record ReadArtifactResult
{
    public required string Name { get; init; }

    public required string Format { get; init; }

    public required int Version { get; init; }

    public required int LineCount { get; init; }

    public required string Content { get; init; }
}
