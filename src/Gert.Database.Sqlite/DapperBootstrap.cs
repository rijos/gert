using Dapper;

namespace Gert.Database.Sqlite;

/// <summary>
/// Single owner of the process-wide Dapper configuration (dotnet-style-guide.md section 8):
/// bind snake_case columns to PascalCase row properties so the private row records
/// map without per-query aliases. With property binding Dapper also narrows SQLite's
/// Int64 to the property type (<c>int?</c>/<c>int</c>) automatically - so no
/// <c>long</c> columns and no casts in the mappers. Invoked from each repository's
/// static constructor; idempotent (a plain flag assignment), so the three call
/// sites cannot race or disagree.
/// </summary>
internal static class DapperBootstrap
{
    /// <summary>Apply the global Dapper settings. Safe to call repeatedly.</summary>
    internal static void EnsureConfigured()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }
}
