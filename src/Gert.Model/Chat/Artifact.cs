namespace Gert.Model.Chat;

/// <summary>
/// A canvas-tab artifact produced during chat - mirrors the <c>artifacts</c>
/// row in a project's <c>chat.db</c> (storage-and-data.md section chat.db).
/// </summary>
public sealed record Artifact
{
    /// <summary>UUID primary key.</summary>
    public required string Id { get; init; }

    public required string ConversationId { get; init; }

    /// <summary>Producing message; null once that message is removed (ON DELETE SET NULL).</summary>
    public string? MessageId { get; init; }

    public required ArtifactKind Kind { get; init; }

    /// <summary>File-style name, e.g. "decision.md", "status.html".</summary>
    public required string Name { get; init; }

    /// <summary>Language hint for code artifacts.</summary>
    public string? Language { get; init; }

    public required string Content { get; init; }

    public int Version { get; init; } = 1;

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}
