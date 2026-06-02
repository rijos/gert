namespace Gert.Model.Events;

/// <summary>
/// <c>artifact</c> — opens a canvas tab (rest-api.md SSE table).
/// </summary>
public sealed record ArtifactEvent : ChatEvent
{
    public required string Id { get; init; }

    public required ArtifactKind Kind { get; init; }

    public required string Name { get; init; }

    public required string Content { get; init; }

    public override ChatEventType Type => ChatEventType.Artifact;
}
