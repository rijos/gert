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

    /// <summary>
    /// Inline image attachments on a user row (<c>[{mime_type, data}]</c>, base64) —
    /// the SPA re-renders the bubble's images from these after a reload.
    /// </summary>
    public IReadOnlyList<MessageAttachment> Attachments { get; init; } = [];

    public string? ModelId { get; init; }

    /// <summary>
    /// Lifecycle (<c>streaming|complete|error|cancelled</c>, orphan rule pre-applied
    /// by the reader). A reload that finds <c>streaming</c> resubscribes from <see cref="Seq"/>.
    /// </summary>
    public required MessageStatus Status { get; init; }

    /// <summary>The row's seq — the resume cursor for a streaming assistant row.</summary>
    public long Seq { get; init; }

    /// <summary>The model's thinking text, when the turn ran with reasoning on. Wire: <c>reasoning</c>.</summary>
    public string? Reasoning { get; init; }

    /// <summary>Completion tokens of the turn. Wire: <c>token_count</c>.</summary>
    public int? TokenCount { get; init; }

    /// <summary>Pure generation wall-clock in ms (tools excluded). Wire: <c>duration_ms</c>.</summary>
    public long? DurationMs { get; init; }

    /// <summary>Context window occupied by the final model round. Wire: <c>context_tokens</c>.</summary>
    public int? ContextTokens { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public IReadOnlyList<ThreadCitation> Citations { get; init; } = [];

    /// <summary>
    /// The message's tool calls projected back into card shape (<c>tool_calls</c>
    /// rows + their citations), so reloading reproduces the cards the live
    /// stream drew. Wire: <c>tools</c>.
    /// </summary>
    public IReadOnlyList<ThreadToolCall> Tools { get; init; } = [];

    /// <summary>Project a <see cref="Message"/> plus the citations and tool calls bound to it.</summary>
    public static ThreadMessage From(
        Message message,
        IReadOnlyList<Citation> citations,
        IReadOnlyList<ThreadToolCall> tools)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(citations);
        ArgumentNullException.ThrowIfNull(tools);

        return new ThreadMessage
        {
            Id = message.Id,
            Role = message.Role,
            Text = message.Content,
            Attachments = message.Attachments ?? [],
            ModelId = message.ModelId,
            Status = message.Status,
            Seq = message.Seq,
            Reasoning = message.Reasoning,
            TokenCount = message.TokenCount,
            DurationMs = message.DurationMs,
            ContextTokens = message.ContextTokens,
            CreatedAt = message.CreatedAt,
            Citations = citations
                .OrderBy(c => c.Ordinal)
                .Select(ThreadCitation.From)
                .ToList(),
            Tools = tools,
        };
    }
}
