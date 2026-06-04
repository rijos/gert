using Gert.Model;
using Gert.Model.Chat;

namespace Gert.Api.Contracts;

/// <summary>
/// One message in a <see cref="ThreadResponse"/> — the persisted <c>content</c> is
/// surfaced as <c>text</c> (the field the SPA renders), with its citations bound in.
/// </summary>
public sealed record ThreadMessage
{
    public required string Id { get; init; }

    public required MessageRole Role { get; init; }

    /// <summary>The message body (the persisted <c>content</c>). Wire: <c>text</c>.</summary>
    public required string Text { get; init; }

    public string? ModelId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public IReadOnlyList<ThreadCitation> Citations { get; init; } = [];

    /// <summary>Project a <see cref="Message"/> plus the citations bound to it.</summary>
    public static ThreadMessage From(Message message, IReadOnlyList<Citation> citations)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(citations);

        return new ThreadMessage
        {
            Id = message.Id,
            Role = message.Role,
            Text = message.Content,
            ModelId = message.ModelId,
            CreatedAt = message.CreatedAt,
            Citations = citations
                .OrderBy(c => c.Ordinal)
                .Select(ThreadCitation.From)
                .ToList(),
        };
    }
}
