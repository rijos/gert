namespace Gert.Tools.Builtin;

/// <summary>
/// The model-facing payload of <c>save_memory</c>: the new entry id and title plus
/// a <see cref="Saved"/> flag. The card's <c>Stdout</c> carries the human line.
/// </summary>
public sealed record SaveMemoryResult
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required bool Saved { get; init; }
}
