using Gert.Model.Chat;
using Gert.Model.Dtos;
using Gert.Validation;

namespace Gert.Service.Conversations;

/// <summary>
/// CRUD over a project's conversations, scoped to the caller's folder
/// (rest-api.md section conversations). The <c>pid</c> selects among the caller's own
/// projects; it never widens scope (configuration.md section 2.5).
/// </summary>
public interface IConversationService
{
    /// <summary>
    /// List conversations in a project (id, title, model, updated_at, archived),
    /// newest first. <paramref name="query"/> filters by title (case-insensitive
    /// contains); <paramref name="limit"/> (0 = all, capped at 100) and
    /// <paramref name="offset"/> page the result for the search overlay's
    /// infinite scroll.
    /// </summary>
    Task<IReadOnlyList<Conversation>> ListAsync(
        string pid,
        string? query = null,
        int limit = 0,
        int offset = 0,
        CancellationToken cancellationToken = default);

    /// <summary>Load a full thread: messages, tool calls, citations, artifacts.</summary>
    Task<ConversationThread?> GetAsync(
        string pid,
        string conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>Create a conversation; unset fields inherit the project/user defaults.</summary>
    Task<Conversation> CreateAsync(
        string pid,
        Validated<CreateConversationRequest> request,
        CancellationToken cancellationToken = default);

    /// <summary>Apply a partial update (rename / switch model / toggle tools / archive).</summary>
    Task<Conversation?> UpdateAsync(
        string pid,
        string conversationId,
        Validated<UpdateConversationRequest> request,
        CancellationToken cancellationToken = default);

    /// <summary>Delete a conversation and cascade its messages/tool calls/citations/artifacts.</summary>
    Task<bool> DeleteAsync(
        string pid,
        string conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Move a conversation (messages, tool calls, citations, artifacts) to
    /// another of the caller's projects. Null when the source conversation does
    /// not exist; throws <c>TurnInProgressException</c> while a turn streams.
    /// </summary>
    Task<Conversation?> MoveAsync(
        string pid,
        string conversationId,
        Validated<MoveConversationRequest> request,
        CancellationToken cancellationToken = default);
}
