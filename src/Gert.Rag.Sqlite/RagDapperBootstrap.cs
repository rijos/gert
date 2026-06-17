using Dapper;

namespace Gert.Rag.Sqlite;

/// <summary>
/// Single owner of this assembly's Dapper configuration (dotnet-style-guide.md section 8):
/// bind snake_case columns to PascalCase row properties so the private row records map without
/// per-query aliases, and let Dapper narrow SQLite's Int64 to the property type. Invoked from
/// <see cref="SqliteRagStore"/>'s static constructor; idempotent (a plain flag assignment). A
/// self-contained copy in the RAG engine leaf - the setting is process-wide so it agrees with
/// the identical bootstrap in <c>Gert.Database.Sqlite</c>.
/// </summary>
internal static class RagDapperBootstrap
{
    /// <summary>Apply the global Dapper settings. Safe to call repeatedly.</summary>
    internal static void EnsureConfigured()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }
}
