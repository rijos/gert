using Gert.Model.Chat;

namespace Gert.Model.Dtos;

/// <summary>
/// Body of <c>POST /api/projects/{pid}/conversations/{id}/messages</c>
/// (rest-api.md § sending a message). Unset <see cref="ModelId"/> /
/// <see cref="Tools"/> inherit the conversation defaults.
/// </summary>
public sealed record SendMessageRequest
{
    public required string Content { get; init; }

    /// <summary>
    /// Inline image attachments (pasted into the composer), sent upstream to
    /// vision-capable models. With attachments present, <see cref="Content"/>
    /// may be empty — an image alone is a valid message.
    /// </summary>
    public IReadOnlyList<MessageAttachment>? Attachments { get; init; }

    public string? ModelId { get; init; }

    public ToolToggles? Tools { get; init; }

    /// <summary>
    /// Reasoning on/off for this turn (<c>chat_template_kwargs.enable_thinking</c>).
    /// Unset inherits the conversation's preference, then the model default.
    /// A supplied value is persisted onto the conversation.
    /// </summary>
    public bool? Thinking { get; init; }

    /// <summary>
    /// Interleaved thinking: send prior turns' reasoning back upstream
    /// (<c>chat_template_kwargs.preserve_thinking</c> + assistant
    /// <c>reasoning_content</c> history). Same inherit/persist semantics as
    /// <see cref="Thinking"/>.
    /// </summary>
    public bool? PreserveThinking { get; init; }
}
