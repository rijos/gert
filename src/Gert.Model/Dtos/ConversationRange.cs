using Gert.Model.Events;

namespace Gert.Model.Dtos;

/// <summary>
/// One page of a conversation's event log (rest-api.md § range endpoint):
/// <c>GET …/conversations/{id}/events?after={seq}&amp;limit={n}</c>. The
/// catch-up/resume/poll read model — always served from <c>chat.db</c>, never
/// from the in-process bus, so it is correct across instances and restarts.
/// </summary>
public sealed record ConversationRange
{
    /// <summary>The events with <c>seq &gt; after</c>, ascending, at most <c>limit</c>.</summary>
    public required IReadOnlyList<TurnEvent> Events { get; init; }

    /// <summary>Pass as the next <c>after</c>; null when <see cref="Events"/> is empty.</summary>
    public long? NextCursor { get; init; }

    /// <summary>True when more events existed beyond this page at read time.</summary>
    public bool HasMore { get; init; }
}
