namespace Gert.Service.Storage;

/// <summary>
/// Database-agnostic options for the storage layer (storage-and-data.md § layout /
/// lazy provisioning). Bound via <c>IOptions&lt;StorageOptions&gt;</c>. Adapter-
/// specific knobs (e.g. the sqlite-vec extension path) live in the adapter's own
/// options class bound to the same <c>Storage</c> section.
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
}
