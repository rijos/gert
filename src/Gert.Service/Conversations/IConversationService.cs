using Gert.Model.Chat;
using Gert.Model.Dtos;

namespace Gert.Service.Conversations;

/// <summary>
/// CRUD over a project's conversations, scoped to the caller's folder
/// (rest-api.md § conversations). The <c>pid</c> selects among the caller's own
/// projects; it never widens scope (configuration.md § 2.5).
/// </summary>
public interface IConversationService
{
    /// <summary>List conversations in a project (id, title, model, updated_at, archived).</summary>
    Task<IReadOnlyList<Conversation>> ListAsync(
        string pid,
        CancellationToken cancellationToken = default);

    /// <summary>Load a full thread: messages, tool calls, citations, artifacts.</summary>
    Task<ConversationThread?> GetAsync(
        string pid,
        string conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>Create a conversation; unset fields inherit the project/user defaults.</summary>
    Task<Conversation> CreateAsync(
        string pid,
        CreateConversationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Apply a partial update (rename / switch model / toggle tools / archive).</summary>
    Task<Conversation?> UpdateAsync(
        string pid,
        string conversationId,
        UpdateConversationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Delete a conversation and cascade its messages/tool calls/citations/artifacts.</summary>
    Task<bool> DeleteAsync(
        string pid,
        string conversationId,
        CancellationToken cancellationToken = default);
}
