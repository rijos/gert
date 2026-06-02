namespace Gert.Database.Sqlite;

/// <summary>
/// Options for the SQLite storage layer (storage-and-data.md § layout / lazy
/// provisioning). Bound via <c>IOptions&lt;StorageOptions&gt;</c>.
/// </summary>
public sealed class StorageOptions
{
    /// <summary>Configuration section name for binding.</summary>
    public const string SectionName = "Storage";

    /// <summary>
    /// Filesystem root that contains the <c>users/</c> tree
    /// (storage-and-data.md § layout). Every per-user folder lives under
    /// <c>{DataRoot}/users/{key}</c>.
    /// </summary>
    public string DataRoot { get; set; } = string.Empty;

    /// <summary>
    /// The single configured token authority. <see cref="ExpectedIssuer"/> is the
    /// fail-closed gate's <c>iss</c> assertion in <c>EnsureProvisioned</c>
    /// (security F12 / decisions §3): an identity whose <c>iss</c> differs is
    /// rejected <b>before any folder is created</b>.
    /// </summary>
    public string ExpectedIssuer { get; set; } = string.Empty;
}
