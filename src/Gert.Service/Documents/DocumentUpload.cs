namespace Gert.Service.Documents;

/// <summary>
/// A pending upload handed to <see cref="IDocumentService.UploadAsync"/>. The
/// host adapts its transport (multipart form / file path) into this shape so the
/// service stays transport-agnostic.
/// </summary>
public sealed record DocumentUpload
{
    public required string Filename { get; init; }

    public required string Mime { get; init; }

    /// <summary>Opens the raw upload bytes for storage + extraction.</summary>
    public required Func<Stream> OpenReadStream { get; init; }

    public long? SizeBytes { get; init; }
}
