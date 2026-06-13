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
    /// Inline image attachments (pasted into the composer), sent upstream to
    /// vision-capable models. With attachments present, <see cref="Content"/>
    /// may be empty - an image alone is a valid message.
    /// </summary>
    public IReadOnlyList<MessageAttachment>? Attachments { get; init; }

    /// <summary>
    /// Provider id for this turn (the <c>Gert:Providers</c> slug). Unset inherits the
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
