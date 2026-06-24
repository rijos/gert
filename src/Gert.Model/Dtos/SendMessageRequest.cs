using Gert.Model.Chat;

namespace Gert.Model.Dtos;

/// <summary>
/// Body of <c>POST /api/projects/{pid}/conversations/{id}/messages</c>
/// (rest-api.md section sending a message). Unset <see cref="ModelId"/> /
/// <see cref="Tools"/> inherit the conversation defaults.
/// </summary>
public sealed record SendMessageRequest
{
    public required string Content { get; init; }

    /// <summary>
    /// Inline attachments (image or text-file) pasted or dropped into the
    /// composer; null/empty when none. See <see cref="MessageAttachment"/> /
    /// <see cref="AttachmentKinds"/> for the per-attachment kind discrimination.
    /// With attachments present, <see cref="Content"/> may be empty - an
    /// attachment alone is a valid message.
    /// </summary>
    public IReadOnlyList<MessageAttachment>? Attachments { get; init; }

    /// <summary>
    /// Provider id for this turn (the <c>Gert:Chat:Providers</c> slug). Unset inherits the
    /// conversation's provider. Sampling + thinking ride the provider, not the request.
    /// </summary>
    public string? ModelId { get; init; }

    public ToolToggles? Tools { get; init; }

    /// <summary>
    /// The caller's IANA timezone (the browser's
    /// <c>Intl.DateTimeFormat().resolvedOptions().timeZone</c>) - makes the
    /// clock tool answer in the user's local time by default. Optional;
    /// unset means UTC.
    /// </summary>
    public string? Timezone { get; init; }
}
