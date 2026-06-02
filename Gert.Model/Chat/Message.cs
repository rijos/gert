namespace Gert.Model.Chat;

/// <summary>
/// A single message in a conversation — mirrors the <c>messages</c> row in a
/// project's <c>chat.db</c> (storage-and-data.md § chat.db).
/// </summary>
public sealed record Message
{
    /// <summary>UUID primary key.</summary>
    public required string Id { get; init; }

    public required string ConversationId { get; init; }

    public required MessageRole Role { get; init; }

    public required string Content { get; init; }

    /// <summary>Model that produced this message; null for user/system rows.</summary>
    public string? ModelId { get; init; }

    public int? TokenCount { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}
