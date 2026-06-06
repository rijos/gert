using Gert.Model.Chat;
using Gert.Model.Dtos;
using Gert.Database;

namespace Gert.Service.Conversations;

/// <summary>
/// CRUD over a project's conversations, scoped to the caller's identity
/// (rest-api.md § conversations). Every operation resolves the repository via
/// <see cref="IDatabaseProvider.OpenChatAsync"/> using the
/// <see cref="IUserContext"/>'s <c>(iss, sub)</c> and the supplied
/// <c>pid</c> — the caller never supplies the user, so a request structurally
/// cannot widen scope to another user's folder (configuration.md § 2.5,
/// principles.md #6).
/// </summary>
public sealed class ConversationService : IConversationService
{
    private readonly IDatabaseProvider _databases;
    private readonly IUserContext _user;

    /// <summary>The fallback model id when nothing in the cascade supplies one.</summary>
    private const string DefaultModelId = "default";

    public ConversationService(IDatabaseProvider databases, IUserContext user)
    {
        _databases = databases ?? throw new ArgumentNullException(nameof(databases));
        _user = user ?? throw new ArgumentNullException(nameof(user));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Conversation>> ListAsync(
        string pid,
        CancellationToken cancellationToken = default)
    {
        await using var repo = await OpenAsync(pid, cancellationToken).ConfigureAwait(false);
        return await repo.ListConversationsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ConversationThread?> GetAsync(
        string pid,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        await using var repo = await OpenAsync(pid, cancellationToken).ConfigureAwait(false);
        return await repo.GetThreadAsync(conversationId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Conversation> CreateAsync(
        string pid,
        CreateConversationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = DateTimeOffset.UtcNow;
        var conversation = new Conversation
        {
            Id = Guid.NewGuid().ToString("D"),
            Title = string.IsNullOrWhiteSpace(request.Title) ? "New conversation" : request.Title,
            // TODO U7b/U6: resolve the model/tools/params cascade
            // (conversation → project defaults → user settings → server). Keep simple for M1.
            ModelId = string.IsNullOrWhiteSpace(request.ModelId) ? DefaultModelId : request.ModelId,
            Tools = request.Tools ?? new ToolToggles(),
            Params = request.Params ?? new GenerationParams(),
            CreatedAt = now,
            UpdatedAt = now,
            Archived = false,
        };

        await using var repo = await OpenAsync(pid, cancellationToken).ConfigureAwait(false);
        await repo.InsertConversationAsync(conversation, cancellationToken).ConfigureAwait(false);
        return conversation;
    }

    /// <inheritdoc />
    public async Task<Conversation?> UpdateAsync(
        string pid,
        string conversationId,
        UpdateConversationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var repo = await OpenAsync(pid, cancellationToken).ConfigureAwait(false);

        var existing = await repo.GetConversationAsync(conversationId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        // Apply only the supplied subset (PATCH semantics); bump updated_at.
        var updated = existing with
        {
            Title = request.Title ?? existing.Title,
            ModelId = request.ModelId ?? existing.ModelId,
            Tools = request.Tools ?? existing.Tools,
            Params = request.Params ?? existing.Params,
            Archived = request.Archived ?? existing.Archived,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await repo.UpdateConversationAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        string pid,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        await using var repo = await OpenAsync(pid, cancellationToken).ConfigureAwait(false);
        return await repo.DeleteConversationAsync(conversationId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Open the project's chat repository for the *current user* — the identity
    /// always comes from <see cref="IUserContext"/>, never from a parameter.
    /// </summary>
    private Task<IChatRepository> OpenAsync(string pid, CancellationToken cancellationToken) =>
        _databases.OpenChatAsync(_user.Iss, _user.Sub, pid, cancellationToken);
}
