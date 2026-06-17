namespace Gert.Model.Chat;

/// <summary>
/// A conversation thread - mirrors the <c>conversations</c> row in a project's
/// <c>chat.db</c> (storage-and-data.md section chat.db). Per-conversation tool toggles
/// are persisted as the <c>tools_json</c> JSON column (<see cref="Dtos.ToolToggles"/>);
/// sampling rides the selected provider, not the conversation
/// (configuration.md section providers).
/// </summary>
public sealed record Conversation
{
    /// <summary>UUID primary key.</summary>
    public required string Id { get; init; }

    public required string Title { get; init; }

    /// <summary>
    /// Selected provider id - the <c>Gert:Chat:Providers</c> slug (or the
    /// <see cref="ChatProviderInfo.DefaultId"/> sentinel). The provider fixes the
    /// upstream model, connection, and sampling.
    /// </summary>
    public required string ModelId { get; init; }

    /// <summary>Per-conversation tool toggles (the <c>tools_json</c> column).</summary>
    public Dtos.ToolToggles Tools { get; init; } = new();

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public bool Archived { get; init; }
}
