namespace Gert.Model.Chat;

/// <summary>
/// The kind classifier for a <see cref="MessageAttachment"/>, shared by the validator and the
/// prompt-injection path so both agree on routing. The gate is the vision allowlist: only an
/// <b>allowlisted</b> image MIME (<see cref="IsAllowedImageMime"/>) rides to a vision model as a
/// binary image part; anything else - including a non-allowlisted <c>image/*</c> such as
/// <c>image/svg+xml</c> (which can carry script) - is treated as a text-file attachment (decoded
/// into a fenced text block). <see cref="IsImage"/> is the broad "is it image-typed" test;
/// vision routing keys off the allowlist, never off <see cref="IsImage"/>.
/// </summary>
public static class AttachmentKinds
{
    // The image MIME types forwarded to a vision model as a binary part (the composer's paste set).
    // Ordinal, exact-match: a non-allowlisted image/* is deliberately NOT a vision part - it falls
    // to the text-file path like any other named file, so this list is the single routing gate.
    private static readonly string[] AllowedImageMimeTypes =
        ["image/png", "image/jpeg", "image/webp", "image/gif"];

    /// <summary>True if <paramref name="mimeType"/> is image-typed (any <c>image/*</c>).</summary>
    public static bool IsImage(string? mimeType) =>
        mimeType is not null && mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True if <paramref name="mimeType"/> is an allowlisted vision image - the only image kind sent
    /// to a vision-capable model as a binary part. The validator and <c>TurnPlanner</c> both route
    /// off this, so a non-allowlisted <c>image/*</c> can never reach the model as an image.
    /// </summary>
    public static bool IsAllowedImageMime(string? mimeType) =>
        mimeType is not null && Array.IndexOf(AllowedImageMimeTypes, mimeType) >= 0;
}
