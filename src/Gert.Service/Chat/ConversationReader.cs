using System.Text.Json;
using Gert.Model.Chat;
using Gert.Model.Dtos;
using Gert.Model.Events;
using Gert.Model.Json;
using Gert.Database;
using Microsoft.Extensions.Options;

namespace Gert.Service.Chat;

/// <summary>
/// <see cref="IConversationReader"/> over the per-project <c>chat.db</c>
/// (open-per-use, identity from <see cref="IUserContext"/> — the caller never
/// supplies the user, configuration.md § 2.5). Applies
/// <see cref="MessageStatusRules"/> so orphaned <c>streaming</c> rows read as
/// error everywhere, not just in one transport.
/// </summary>
public sealed class ConversationReader : IConversationReader
{
    /// <summary>Cap on one range page; clients page with the cursor.</summary>
    public const int MaxLimit = 1000;

    private readonly IDatabaseProvider _databases;
    private readonly IUserContext _user;
    private readonly TurnOptions _options;

    public ConversationReader(
        IDatabaseProvider databases,
        IUserContext user,
        IOptions<TurnOptions> options)
    {
        _databases = databases ?? throw new ArgumentNullException(nameof(databases));
        _user = user ?? throw new ArgumentNullException(nameof(user));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public async Task<ConversationRange> ReadRangeAsync(
        string pid,
        string conversationId,
        long afterSeq,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pid);
        ArgumentNullException.ThrowIfNull(conversationId);

        var capped = Math.Clamp(limit, 1, MaxLimit);

        await using var repo = await _databases
            .OpenChatAsync(_user.Iss, _user.Sub, pid, cancellationToken)
            .ConfigureAwait(false);

        // Read one extra row to learn whether more exist beyond this page.
        var rows = await repo
            .ReadTurnEventsAsync(conversationId, afterSeq, capped + 1, cancellationToken)
            .ConfigureAwait(false);

        var hasMore = rows.Count > capped;
        var page = hasMore ? rows.Take(capped).ToList() : rows;

        var events = new List<TurnEvent>(page.Count);
        foreach (var row in page)
        {
            events.Add(new TurnEvent { Seq = row.Seq, Event = Deserialize(row) });
        }

        return new ConversationRange
        {
            Events = events,
            NextCursor = events.Count > 0 ? events[^1].Seq : null,
            HasMore = hasMore,
        };
    }

    /// <inheritdoc />
    public async Task<ConversationThread?> GetThreadAsync(
        string pid,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pid);
        ArgumentNullException.ThrowIfNull(conversationId);

        await using var repo = await _databases
            .OpenChatAsync(_user.Iss, _user.Sub, pid, cancellationToken)
            .ConfigureAwait(false);

        var thread = await repo.GetThreadAsync(conversationId, cancellationToken).ConfigureAwait(false);
        if (thread is null)
        {
            return null;
        }

        // Orphan rule: report abandoned streaming rows as error (one home:
        // MessageStatusRules — keep this the only call site shape).
        var now = DateTimeOffset.UtcNow;
        var messages = thread.Messages
            .Select(m => m with { Status = MessageStatusRules.Effective(m, now, _options.MaxTurnDuration) })
            .ToList();

        return thread with { Messages = messages };
    }

    private static ChatEvent Deserialize(TurnEventRecord row)
    {
        try
        {
            return JsonSerializer.Deserialize<ChatEvent>(row.PayloadJson, GertJsonOptions.Default)
                ?? throw new InvalidOperationException("null payload");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            throw new InvalidOperationException(
                $"Corrupt turn_events row (conversation '{row.ConversationId}', seq {row.Seq}): {ex.Message}", ex);
        }
    }
}
