namespace Gert.Database;

/// <summary>
/// Releases any OS file handles the database engine still holds under the data
/// root — pooled connections, mmaps — so a directory delete (<c>rm -rf</c> a
/// project/user folder) never operates on unlinked-but-open database files that
/// would otherwise resurface stale rows after re-provisioning.
///
/// <para>
/// File-backed engines need a real implementation (SQLite:
/// <c>SqliteConnection.ClearAllPools()</c>); server-backed engines (e.g. a future
/// Postgres adapter) hold no handles under the data root and register a no-op.
/// Called by the file layer (<c>IUserStore</c> adapter) before destructive
/// directory operations.
/// </para>
/// </summary>
public interface IDatabaseHandleReleaser
{
    /// <summary>Release all engine-held file handles under the data root.</summary>
    void ReleaseAll();
}
