using Gert.Model.Chat;

namespace Gert.Service.Database;

/// <summary>
/// Per-project <c>chat.db</c> persistence (storage-and-data.md § chat.db). One
/// instance wraps an open connection to a single project's database; dispose it
/// when the unit of work completes (open-per-use). The path is the scope — there
/// is no <c>project_id</c>/<c>user</c> argument, so a query structurally cannot
/// reach another project's rows.
/// </summary>
public interface IChatRepository : IAsyncDisposable
{
    // Conversations
    Task<IReadOnlyList<Conversation>> ListConversationsAsync(CancellationToken cancellationToken = default);

    Task<Conversation?> GetConversationAsync(string conversationId, CancellationToken cancellationToken = default);

    Task<ConversationThread?> GetThreadAsync(string conversationId, CancellationToken cancellationToken = default);

    Task InsertConversationAsync(Conversation conversation, CancellationToken cancellationToken = default);

    Task UpdateConversationAsync(Conversation conversation, CancellationToken cancellationToken = default);

    Task<bool> DeleteConversationAsync(string conversationId, CancellationToken cancellationToken = default);

    // Messages
    Task<IReadOnlyList<Message>> ListMessagesAsync(string conversationId, CancellationToken cancellationToken = default);

    Task InsertMessageAsync(Message message, CancellationToken cancellationToken = default);

    // Tool calls
    Task InsertToolCallAsync(ToolCall toolCall, CancellationToken cancellationToken = default);

    // Citations
    Task InsertCitationsAsync(IReadOnlyList<Citation> citations, CancellationToken cancellationToken = default);

    // Artifacts
    Task<IReadOnlyList<Artifact>> ListArtifactsAsync(string conversationId, CancellationToken cancellationToken = default);

    Task<Artifact?> GetArtifactAsync(string artifactId, CancellationToken cancellationToken = default);

    Task InsertArtifactAsync(Artifact artifact, CancellationToken cancellationToken = default);
}
