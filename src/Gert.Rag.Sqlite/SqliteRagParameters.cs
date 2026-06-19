namespace Gert.Rag.Sqlite;

/// <summary>
/// SQLite RAG-engine parameters, bound to <c>Gert:Rag:Parameters</c> - the impl-private
/// knobs of the sqlite-vec + FTS5 index (configuration.md section 1: connection / impl
/// config lives under <c>Parameters</c>). The engine itself is selected by
/// <c>Gert:Rag:Type</c>; this carries the index's data root and the native-extension
/// location.
/// </summary>
public sealed class SqliteRagParameters
{
    public const string SectionName = "Gert:Rag:Parameters";

    /// <summary>
    /// Filesystem root for the RAG index, holding each project's <c>rag.db</c> at
    /// <c>{DataRoot}/users/{key}/projects/{pid}/rag.db</c>. When null/blank the engine
    /// falls back to the shared <see cref="Gert.Storage.StorageOptions.DataRoot"/>; set it
    /// to place the vector index on its own volume, independent of the structured
    /// databases (<c>Gert:Database:Parameters:DataRoot</c>) and the object store
    /// (<c>Storage:DataRoot</c>).
    /// </summary>
    public string? DataRoot { get; set; }

    /// <summary>
    /// Filesystem path to the native <b>sqlite-vec</b> loadable extension
    /// (<c>vec0.so</c> / <c>vec0.dll</c>), loaded on every <c>rag.db</c> connection
    /// (chat-and-tools.md section "Loading sqlite-vec in .NET"). When null, the provider
    /// falls back to <c>vec0.so</c> beside the running assembly
    /// (<c>AppContext.BaseDirectory</c>) - the csproj copies the vendored extension there,
    /// and it flows transitively into every consumer's output.
    /// </summary>
    public string? VecExtensionPath { get; set; }
}
