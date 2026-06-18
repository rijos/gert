using Gert.Database;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Model.Dtos;
using Gert.Service.Validation;

namespace Gert.Service.Conversations;

/// <summary>
/// CRUD over a project's conversations, scoped to the caller's identity
/// (rest-api.md section conversations). Every operation resolves the repository via
/// <see cref="IChatDatabaseProvider.OpenAsync"/> using the
/// <see cref="IUserContext"/>'s <c>(iss, sub)</c> and the supplied
/// <c>pid</c> - the caller never supplies the user, so a request structurally
/// cannot widen scope to another user's folder (configuration.md section 2.5,
/// principles.md #6).
/// </summary>
public sealed class ConversationService : IConversationService
{
    private readonly IChatDatabaseProvider _databases;
    private readonly IUserContext _user;
    private readonly TimeProvider _time;

    public ConversationService(
        IChatDatabaseProvider databases,
        IUserContext user,
        TimeProvider time)
    {
        _databases = databases ?? throw new ArgumentNullException(nameof(databases));
        _user = user ?? throw new ArgumentNullException(nameof(user));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>Page-size ceiling for the search overlay (defensive clamp).</summary>
    private const int MaxPageSize = 100;

    /// <inheritdoc />
    public async Task<IReadOnlyList<Conversation>> ListAsync(
        string pid,
        string? query = null,
        int limit = 0,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        await using var repo = await OpenAsync(pid, cancellationToken).ConfigureAwait(false);
        var all = await repo.ListConversationsAsync(cancellationToken).ConfigureAwait(false);

        // Filter + slice in memory: a project's conversation registry is small
        // (per-user SQLite), so a SQL-side LIKE would buy nothing but repo surface.
        IEnumerable<Conversation> page = all.OrderByDescending(c => c.UpdatedAt);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var needle = query.Trim();
            page = page.Where(c => (c.Title ?? string.Empty)
                .Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        if (offset > 0)
        {
            page = page.Skip(offset);
        }

        if (limit > 0)
        {
            page = page.Take(Math.Min(limit, MaxPageSize));
        }

        return page.ToList();
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
        Validated<CreateConversationRequest> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var dto = request.Value;

        // Injected clock (dotnet-style-guide.md section 5) so tests can pin the timestamps.
        var now = _time.GetUtcNow();
        var conversation = new Conversation
        {
            Id = Guid.NewGuid().ToString("D"),
            Title = string.IsNullOrWhiteSpace(dto.Title) ? "New conversation" : dto.Title,
            // TODO: resolve the model/tools cascade (conversation -> project
            // defaults -> user settings -> server). Sampling is no
            // longer a cascade level - it rides the selected provider (Gert:Chat:Providers).
            ModelId = string.IsNullOrWhiteSpace(dto.ModelId) ? ChatProviderInfo.DefaultId : dto.ModelId,
            Tools = dto.Tools ?? new ToolToggles(),
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
        Validated<UpdateConversationRequest> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var dto = request.Value;

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
            Title = dto.Title ?? existing.Title,
            ModelId = dto.ModelId ?? existing.ModelId,
            Tools = dto.Tools ?? existing.Tools,
            Archived = dto.Archived ?? existing.Archived,
            UpdatedAt = _time.GetUtcNow(),
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

    /// <inheritdoc />
    public async Task<Conversation?> MoveAsync(
        string pid,
        string conversationId,
        Validated<MoveConversationRequest> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var dto = request.Value;

        await using var source = await OpenAsync(pid, cancellationToken).ConfigureAwait(false);
        var thread = await source.GetThreadAsync(conversationId, cancellationToken).ConfigureAwait(false);
        if (thread is null)
        {
            return null;
        }

        // Moving to itself is a no-op, not an error (an idempotent retry).
        if (string.Equals(dto.TargetPid, pid, StringComparison.Ordinal))
        {
            return thread.Conversation;
        }

        // A streaming turn owns the conversation: its runner finalizes rows in
        // the SOURCE database - moving underneath it would strand the write-back.
        if (thread.Messages.Any(m => m.Status == MessageStatus.Streaming))
        {
            throw new Chat.TurnInProgressException(conversationId);
        }

        await using var target = await OpenAsync(dto.TargetPid, cancellationToken).ConfigureAwait(false);
        if (await target.GetConversationAsync(conversationId, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new ValidationException(ValidationResult.Failure(
            [
                new ValidationError
                {
                    Property = "target_pid",
                    Message = "The target project already contains this conversation.",
                    Code = "target_pid.conflict",
                },
            ]));
        }

        // Copy-then-delete, target first: a failure mid-copy compensates by
        // dropping the half-copied target (delete cascades), and the source is
        // only deleted once the target holds the full thread - the conversation
        // can never be lost, at worst briefly duplicated. Messages are
        // re-sequenced through the target's own counter (seq is an ordering
        // cursor, not identity); ids (conversation/message/tool call/artifact)
        // travel unchanged so provenance survives. The turn-event log stays
        // behind by design - it serves live resume, which a finished thread
        // doesn't need (the thread read model carries everything the UI reloads).
        var moved = thread.Conversation with { UpdatedAt = _time.GetUtcNow() };
        await target.InsertConversationAsync(moved, cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var message in thread.Messages.OrderBy(m => m.Seq))
            {
                var seq = await target.AllocateSeqAsync(conversationId, cancellationToken).ConfigureAwait(false);
                await target.InsertMessageAsync(message with { Seq = seq }, cancellationToken).ConfigureAwait(false);
            }

            foreach (var toolCall in thread.ToolCalls)
            {
                await target.InsertToolCallAsync(toolCall, cancellationToken).ConfigureAwait(false);
            }

            if (thread.Citations.Count > 0)
            {
                await target.InsertCitationsAsync(thread.Citations, cancellationToken).ConfigureAwait(false);
            }

            foreach (var artifact in thread.Artifacts)
            {
                await target.InsertArtifactAsync(artifact, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // CancellationToken.None: the compensation must run even when the
            // failure IS a cancel.
            await target.DeleteConversationAsync(conversationId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await source.DeleteConversationAsync(conversationId, cancellationToken).ConfigureAwait(false);
        return moved;
    }

    /// <summary>
    /// Open the project's chat repository for the *current user* - the identity
    /// always comes from <see cref="IUserContext"/>, never from a parameter.
    /// </summary>
    private Task<IChatRepository> OpenAsync(string pid, CancellationToken cancellationToken) =>
        _databases.OpenAsync(_user.Iss, _user.Sub, pid, cancellationToken);
}
