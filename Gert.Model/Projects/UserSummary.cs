namespace Gert.Model.Projects;

/// <summary>
/// An admin's view of one user folder — the <c>GET /api/admin/users</c> shape
/// (rest-api.md § admin), built by reading each folder's <c>meta.json</c>. The
/// closest thing to a "user list" the API has; it never opens another user's
/// <c>chat.db</c>/<c>rag.db</c>.
/// </summary>
public sealed record UserSummary
{
    /// <summary>Folder key — <c>sha256(iss + sub)</c> hex.</summary>
    public required string Key { get; init; }

    public required string Username { get; init; }

    public long SizeBytes { get; init; }

    public int DocumentCount { get; init; }

    public DateTimeOffset? LastActive { get; init; }
}
