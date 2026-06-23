namespace Gert.Model.Chat;

/// <summary>
/// The kind discriminator for a <see cref="MessageAttachment"/>: an <c>image/*</c> MIME is an image
/// (vision part), anything else is a text-file attachment (fenced text part). The single place that
/// classifies an attachment, shared by the validator and the prompt-injection so both agree.
/// </summary>
public static class AttachmentKinds
{
    /// <summary>True if <paramref name="mimeType"/> is an image (handled as a vision attachment).</summary>
    public static bool IsImage(string? mimeType) =>
        mimeType is not null && mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
}
