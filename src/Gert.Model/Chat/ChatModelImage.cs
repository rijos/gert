namespace Gert.Model.Chat;

/// <summary>
/// One image riding a user message upstream - serialized by the request builder
/// as an OpenAI-style <c>image_url</c> content part with a base64 data URL.
/// </summary>
public sealed record ChatModelImage
{
    /// <summary>Image MIME type (<c>image/png</c>, ...).</summary>
    public required string MimeType { get; init; }

    /// <summary>The image bytes as plain base64 (no <c>data:</c> prefix).</summary>
    public required string DataBase64 { get; init; }
}
