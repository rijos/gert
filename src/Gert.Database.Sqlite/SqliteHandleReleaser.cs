using Gert.Service.Database;
using Microsoft.Data.Sqlite;

namespace Gert.Database.Sqlite;

/// <summary>
/// <see cref="IDatabaseHandleReleaser"/> for SQLite: drops every pooled
/// connection handle so <c>rm -rf</c> of a project/user folder never operates on
/// unlinked-but-open <c>chat.db</c>/<c>rag.db</c> files (open-per-use returns
/// connections to Microsoft.Data.Sqlite's internal pool, which would otherwise
/// keep the deleted file alive and resurface stale rows after re-provisioning).
/// </summary>
public sealed class SqliteHandleReleaser : IDatabaseHandleReleaser
{
    /// <inheritdoc />
    public void ReleaseAll() => SqliteConnection.ClearAllPools();
}
