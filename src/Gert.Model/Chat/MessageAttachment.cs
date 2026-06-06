namespace Gert.Model.Chat;

/// <summary>
/// One inline image attached to a (user) message — pasted into the composer and
/// sent upstream to vision-capable models as an OpenAI-style <c>image_url</c>
/// data URL. Persisted as JSON in <c>messages.attachments_json</c> and surfaced
/// verbatim on the thread GET so the SPA can re-render the bubble after reload.
/// Inline base64 (no separate blob store) keeps the row self-contained: every
/// upstream call needs the full bytes anyway, and the composer downscales
/// before sending so rows stay small.
/// </summary>
public sealed record MessageAttachment
{
    /// <summary>Image MIME type (<c>image/png</c>, <c>image/jpeg</c>, <c>image/webp</c>, <c>image/gif</c>).</summary>
    public required string MimeType { get; init; }

    /// <summary>The image bytes as plain base64 (no <c>data:</c> prefix).</summary>
    public required string Data { get; init; }
}
