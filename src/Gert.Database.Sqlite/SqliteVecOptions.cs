namespace Gert.Database.Sqlite;

/// <summary>
/// SQLite-adapter-specific storage options, bound to the same <c>Storage</c>
/// configuration section as the shared <see cref="Gert.Service.Storage.StorageOptions"/>
/// (the binder ignores keys it doesn't own, so one section feeds both).
/// </summary>
public sealed class SqliteVecOptions
{
    /// <summary>Configuration section name for binding (shared with <c>StorageOptions</c>).</summary>
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
}
