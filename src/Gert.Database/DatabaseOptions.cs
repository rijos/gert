namespace Gert.Database;

/// <summary>
/// The database engine selection (<c>Gert:Database</c>): the uniform
/// "functionality -> Type" shape (configuration.md section 4; tech-stack.md section
/// Architecture). One engine sits behind the database providers
/// (<see cref="IUserDatabaseProvider"/> / <see cref="IChatDatabaseProvider"/>):
/// <c>Sqlite</c> ships today (per-user SQLite files; the default). The RAG/vector index
/// is a separate capability (<c>Gert.Rag</c>, <c>Gert:Rag:Type</c>), not a database
/// provider. A future server engine (<c>Postgres</c>) is a sibling
/// <c>Gert.Database.*</c> plugin selected by the same <see cref="Type"/> token, with
/// no central <c>switch</c>. Case-insensitive; a value with no registered plugin fails
/// fast at first resolution (see <see cref="DatabaseEngineFactory"/>).
///
/// <para>
/// There is no <c>Parameters</c> bag here: a file-backed engine reads the shared
/// <c>Storage</c> data-root for its db-file paths, and a server engine's connection
/// string is a secret (F8) that lives in env / user-secrets, not here.
/// </para>
/// </summary>
public sealed class DatabaseOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Gert:Database";

    /// <summary>
    /// Which engine the database providers resolve to: <c>Sqlite</c> (default - per-user
    /// SQLite files under the <c>Storage</c> data-root). Case-insensitive; an unknown
    /// value fails fast at first resolution with an actionable message.
    /// </summary>
    public string Type { get; set; } = DatabaseEngineFactory.DefaultType;
}
