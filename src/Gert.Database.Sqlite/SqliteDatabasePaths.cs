using Gert.Storage;
using Microsoft.Extensions.Options;

namespace Gert.Database.Sqlite;

/// <summary>
/// Resolves the LOCAL filesystem paths the SQLite adapter needs - the
/// per-<c>(iss, sub)</c> user folder and its per-<c>pid</c> project database files
/// (storage-and-data.md section resolving paths). SQLite-only by design: a server-backed
/// engine (e.g. Postgres) has a connection string, not paths. The folder key is
/// <c>sha256(iss + "\n" + sub)</c> lowercase hex (decisions section 3) - fixed-length,
/// path-safe, and traversal-proof for any value the IdP emits.
///
/// <para>
/// <c>(iss, sub)</c> come only from the validated token; the fail-closed
/// provisioning gate vets them <b>before</b> any of these methods run. The <c>pid</c> comes from
/// the request, so it is validated to a UUID or the literal <c>default</c> and is
/// only ever joined <b>under</b> the user root - it can select among this user's
/// projects but can never escape the user folder (cross-user IDOR is structurally
/// impossible). See storage-and-data.md section resolving paths and security F12.
/// </para>
/// </summary>
public sealed class SqliteDatabasePaths
{
    /// <summary>The literal landing-project id, always present (storage-and-data.md section layout).</summary>
    public const string DefaultProjectId = StorageKeys.DefaultProjectId;

    private readonly string _dataRoot;

    /// <summary>
    /// Resolve paths under this engine's data root: <see cref="SqliteDatabaseParameters.DataRoot"/>
    /// when set, otherwise the shared <see cref="StorageOptions.DataRoot"/>. The structured
    /// databases (<c>user.db</c> + <c>chat.db</c>) can thus live on their own volume,
    /// independent of the RAG index and the object store.
    /// </summary>
    public SqliteDatabasePaths(
        IOptions<StorageOptions> storage,
        IOptions<SqliteDatabaseParameters> parameters)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(parameters);

        var configured = parameters.Value.DataRoot;
        _dataRoot = string.IsNullOrWhiteSpace(configured) ? storage.Value.DataRoot : configured;

        // Mirror LocalObjectStore's fail-fast guard: with neither an engine override nor a
        // shared Storage:DataRoot, paths would silently resolve ./users relative to the CWD.
        if (string.IsNullOrWhiteSpace(_dataRoot))
        {
            throw new InvalidOperationException(
                $"No data root configured: set {SqliteDatabaseParameters.SectionName}:{nameof(SqliteDatabaseParameters.DataRoot)} " +
                $"or {StorageOptions.SectionName}:{nameof(StorageOptions.DataRoot)}.");
        }
    }

    /// <summary>The configured <c>{DataRoot}/users</c> directory.</summary>
    public string UsersDir => Path.Combine(_dataRoot, "users");

    /// <summary>
    /// Folder key - <c>sha256(iss + "\n" + sub)</c> lowercase hex (decisions section 3).
    /// One policy for every adapter: delegates to <see cref="StorageKeys.UserKey"/>.
    /// </summary>
    public static string Key(string iss, string sub) => StorageKeys.UserKey(iss, sub);

    // ---- user-level paths --------------------------------------------------

    /// <summary>The user folder root, <c>{DataRoot}/users/{key}</c>.</summary>
    public string Root(string iss, string sub) => Path.Combine(UsersDir, Key(iss, sub));

    /// <summary>The per-user database <c>user.db</c> (username, settings, project registry).</summary>
    public string UserDb(string iss, string sub) => Path.Combine(Root(iss, sub), "user.db");

    /// <summary>
    /// The user folder root addressed by its validated folder <paramref name="key"/>
    /// (the admin path, which holds the hashed key, never the original identity).
    /// </summary>
    public string RootByKey(string key)
    {
        StorageKeys.ValidateUserKey(key);
        return Path.Combine(UsersDir, key);
    }

    /// <summary>The per-user database addressed by folder <paramref name="key"/> (admin path).</summary>
    public string UserDbByKey(string key) => Path.Combine(RootByKey(key), "user.db");

    /// <summary>
    /// Every database file this engine owns under the user root - <c>user.db</c> plus each
    /// project's <c>chat.db</c> - the database half of a whole-account delete. The RAG index
    /// (<c>rag.db</c>) is a separate engine and removes its own files. Enumerates the on-disk
    /// <c>projects/</c> directory so an orphaned project folder (a db with no registry row) is
    /// caught too.
    /// </summary>
    public IReadOnlyList<string> UserDatabaseFiles(string iss, string sub) =>
        DatabaseFilesUnder(Root(iss, sub));

    /// <summary>As <see cref="UserDatabaseFiles"/> but addressed by folder key (admin path; F6-validated).</summary>
    public IReadOnlyList<string> UserDatabaseFilesByKey(string key) =>
        DatabaseFilesUnder(RootByKey(key));

    private static IReadOnlyList<string> DatabaseFilesUnder(string root)
    {
        var files = new List<string> { Path.Combine(root, "user.db") };

        var projectsDir = Path.Combine(root, "projects");
        if (Directory.Exists(projectsDir))
        {
            foreach (var projectDir in Directory.EnumerateDirectories(projectsDir))
            {
                files.Add(Path.Combine(projectDir, "chat.db"));
            }
        }

        return files;
    }

    // ---- project-level paths (pid validated; never escapes Root) -----------

    /// <summary>
    /// The project folder <c>{Root}/projects/{pid}</c>. Throws
    /// <see cref="ArgumentException"/> if <paramref name="pid"/> is not a UUID or
    /// the literal <c>default</c>, or if the resolved path would escape the user
    /// root (defensive traversal check).
    /// </summary>
    public string ProjectRoot(string iss, string sub, string pid)
    {
        ValidatePid(pid);

        var root = Root(iss, sub);
        var projectRoot = Path.Combine(root, "projects", pid);

        // Defence-in-depth: even though pid is shape-validated above, assert the
        // fully-resolved path stays under the user root - a value can never escape.
        var fullRoot = Path.GetFullPath(root);
        var fullProject = Path.GetFullPath(projectRoot);
        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        if (!fullProject.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Resolved project path '{fullProject}' escapes the user root.", nameof(pid));
        }

        return projectRoot;
    }

    /// <summary>The conversations database <c>projects/{pid}/chat.db</c>.</summary>
    public string ChatDb(string iss, string sub, string pid) =>
        Path.Combine(ProjectRoot(iss, sub, pid), "chat.db");

    /// <summary>
    /// Reject any <paramref name="pid"/> that is not a UUID or the literal
    /// <c>default</c> (configuration.md section 2.5). Rejecting the shape up front means
    /// <c>..</c>, separators and absolute paths never reach <see cref="Path.Combine"/>.
    /// </summary>
    public static void ValidatePid(string pid)
    {
        ArgumentNullException.ThrowIfNull(pid);

        if (pid == DefaultProjectId)
        {
            return;
        }

        if (!Guid.TryParseExact(pid, "D", out _))
        {
            throw new ArgumentException(
                $"Invalid project id '{pid}'; must be a UUID (8-4-4-4-12) or the literal 'default'.",
                nameof(pid));
        }
    }
}
