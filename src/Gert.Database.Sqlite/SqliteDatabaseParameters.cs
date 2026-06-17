namespace Gert.Database.Sqlite;

/// <summary>
/// SQLite database-engine parameters, bound to <c>Gert:Database:Parameters</c> - the
/// impl-private knobs of the file-backed engine (configuration.md section 1: connection /
/// impl config lives under <c>Parameters</c>). Only the SQLite engine reads this; a
/// server engine has a connection string instead.
/// </summary>
public sealed class SqliteDatabaseParameters
{
    /// <summary>The configuration section these parameters bind from.</summary>
    public const string SectionName = "Gert:Database:Parameters";

    /// <summary>
    /// Filesystem root for this engine's databases (<c>user.db</c> + each project's
    /// <c>chat.db</c>), holding the <c>users/</c> tree at <c>{DataRoot}/users/{key}/...</c>.
    /// When null/blank the engine falls back to the shared
    /// <see cref="Gert.Storage.StorageOptions.DataRoot"/>; set it to place the structured
    /// databases on their own volume, independent of the RAG index
    /// (<c>Gert:Rag:Parameters:DataRoot</c>) and the object store (<c>Storage:DataRoot</c>).
    /// </summary>
    public string? DataRoot { get; set; }
}
