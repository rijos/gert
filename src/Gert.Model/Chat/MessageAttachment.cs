namespace Gert.Model.Chat;

/// <summary>
/// One inline attachment on a (user) message. Two kinds, distinguished by
/// <see cref="MimeType"/> (<see cref="AttachmentKinds.IsAllowedImageMime"/>):
/// <list type="bullet">
///   <item><b>Image</b> (an allowlisted image MIME - png/jpeg/webp/gif) - pasted/dropped into the
///   composer and sent upstream to vision-capable models as an OpenAI-style <c>image_url</c> data
///   URL; <see cref="Name"/> is null.</item>
///   <item><b>Text file</b> (any other type, including a non-allowlisted <c>image/*</c> like
///   <c>image/svg+xml</c>) - dropped into the composer for a one-off task ("pretty-format this
///   json"); injected into the prompt as a fenced text block (no vision needed), carrying its
///   <see cref="Name"/>, and rendered as a downloadable chip in history.</item>
/// </list>
/// Persisted as JSON in <c>messages.attachments_json</c> and surfaced verbatim on the thread GET so
/// the SPA can re-render the bubble (and re-offer the file download) after reload. Inline base64
/// (no separate blob store) keeps the row self-contained.
/// </summary>
public sealed record MessageAttachment
{
    /// <summary>The MIME type. An allowlisted image MIME -> a vision image; anything else -> a text file.</summary>
    public required string MimeType { get; init; }

    /// <summary>The raw bytes as plain base64 (no <c>data:</c> prefix).</summary>
    public required string Data { get; init; }

    /// <summary>The original filename for a text-file attachment (display + model context); null for an image.</summary>
    public string? Name { get; init; }
}
