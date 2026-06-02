namespace Gert.Database.Sqlite;

/// <summary>
/// The on-disk user identity binding — the <c>meta.json</c> at the user root
/// (storage-and-data.md § layout): <c>{ iss, sub, username, created_at,
/// schema_version }</c>. The <c>(iss, sub)</c> pair is re-asserted against the
/// token on every request to an existing folder (security F12 identity binding);
/// it never changes once written. <c>username</c> may change in the IdP.
/// </summary>
public sealed record UserMeta
{
    /// <summary>Token issuer — half of the identity binding.</summary>
    public required string Iss { get; init; }

    /// <summary>Stable IdP subject — half of the identity binding (never recycled).</summary>
    public required string Sub { get; init; }

    /// <summary>Human-readable display name; may change in the IdP.</summary>
    public required string Username { get; init; }

    /// <summary>UTC ISO-8601 instant the folder was first provisioned.</summary>
    public required string CreatedAt { get; init; }

    /// <summary>Provisioning schema version stamped at creation.</summary>
    public int SchemaVersion { get; init; } = 1;
}
