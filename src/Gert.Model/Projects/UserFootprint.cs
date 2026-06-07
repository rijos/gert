namespace Gert.Model.Projects;

/// <summary>
/// The blob-storage footprint of one user folder — bytes, document count, and last
/// activity — computed by scanning the object store. The admin scan combines this
/// with the username from <c>user.db</c> to build a <see cref="UserSummary"/>; it
/// never opens another user's <c>chat.db</c>/<c>rag.db</c>.
/// </summary>
public sealed record UserFootprint
{
    /// <summary>Folder key — <c>sha256(iss + "\n" + sub)</c> hex.</summary>
    public required string Key { get; init; }

    /// <summary>Total bytes on disk for this user's folder.</summary>
    public long Size { get; init; }

    public int DocumentCount { get; init; }

    public DateTimeOffset? LastActive { get; init; }
}
