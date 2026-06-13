namespace Gert.Model.Projects;

/// <summary>
/// An admin's view of one user folder - the <c>GET /api/admin/users</c> shape
/// (rest-api.md section admin), built from the folder's blob footprint plus the
/// username from its <c>user.db</c>. The closest thing to a "user list" the API
/// has; it never opens another user's <c>chat.db</c>/<c>rag.db</c>.
/// </summary>
public sealed record UserSummary
{
    /// <summary>Folder key - <c>sha256(iss + sub)</c> hex.</summary>
    public required string Key { get; init; }

    /// <summary>
    /// The username from the user's <c>user.db</c>, or <c>null</c> when the folder
    /// has no username row (e.g. a partially provisioned user) - the folder is
    /// still listed so an admin can see and delete it; the SPA falls back to the key.
    /// </summary>
    public required string? Username { get; init; }

    /// <summary>Total bytes on disk for this user's folder. Wire: <c>size</c>.</summary>
    public long Size { get; init; }

    public int DocumentCount { get; init; }

    public DateTimeOffset? LastActive { get; init; }
}
