namespace Gert.Storage;

/// <summary>
/// The on-disk user metadata sidecar — the <c>meta.json</c> at the user root
/// (storage-and-data.md § layout): <c>{ iss, sub, username, created_at,
/// schema_version }</c>. Purely descriptive — the folder key derives from the
/// validated token, so this file is never a gate: it maps the opaque hash folder
/// to a person for the admin scan and anchors future layout migrations. Rewritten
/// from the token when missing or unreadable. <c>username</c> may change in the IdP.
/// </summary>
public sealed record UserMeta
{
    /// <summary>The layout schema version stamped on newly provisioned folders.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Token issuer recorded at provisioning.</summary>
    public required string Iss { get; init; }

    /// <summary>Stable IdP subject recorded at provisioning (never recycled).</summary>
    public required string Sub { get; init; }

    /// <summary>Human-readable display name; may change in the IdP.</summary>
    public required string Username { get; init; }

    /// <summary>UTC ISO-8601 instant the folder was first provisioned.</summary>
    public required string CreatedAt { get; init; }

    /// <summary>Provisioning schema version stamped at creation.</summary>
    public int SchemaVersion { get; init; } = 1;
}
