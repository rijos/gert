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
    /// Filesystem path to the native <b>sqlite-vec</b> loadable extension
    /// (<c>vec0.so</c> / <c>vec0.dll</c>), loaded on every <c>rag.db</c>
    /// connection (chat-and-tools.md § "Loading sqlite-vec in .NET"). When null,
    /// the provider falls back to <c>vec0.so</c> beside the running assembly
    /// (<c>AppContext.BaseDirectory</c>) — the csproj copies the vendored
    /// extension there, and it flows transitively into every consumer's output.
    /// </summary>
    public string? VecExtensionPath { get; set; }

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
