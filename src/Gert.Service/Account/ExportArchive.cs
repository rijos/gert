namespace Gert.Service.Account;

/// <summary>
/// A produced export - a suggested filename, content type, and a stream factory
/// the host writes to the response. Keeps the service transport-agnostic.
/// </summary>
public sealed record ExportArchive
{
    public required string FileName { get; init; }

    public required string ContentType { get; init; }

    /// <summary>Opens the archive bytes for streaming to the caller.</summary>
    public required Func<CancellationToken, Task<Stream>> OpenReadAsync { get; init; }
}
