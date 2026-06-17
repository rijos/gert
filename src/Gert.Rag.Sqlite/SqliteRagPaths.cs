using Gert.Storage;
using Microsoft.Extensions.Options;

namespace Gert.Rag.Sqlite;

/// <summary>
/// Resolves the LOCAL filesystem paths of a project's <c>rag.db</c> under this engine's data
/// root - <see cref="SqliteRagParameters.DataRoot"/> when set, otherwise the shared
/// <see cref="StorageOptions.DataRoot"/> (storage-and-data.md section resolving paths). The
/// folder key is <c>sha256(iss + "\n" + sub)</c> (<see cref="StorageKeys"/>, core policy) and
/// the <c>pid</c> is shape-validated and asserted to stay <b>under</b> the user root (security
/// F12), so a request value can never escape the user folder. Self-contained in the RAG engine
/// leaf so the RAG capability has no dependency on the SQL database engine - the two are
/// independent stores and may live on separate volumes.
/// </summary>
public sealed class SqliteRagPaths
{
    private readonly string _dataRoot;

    /// <summary>
    /// Resolve paths under this engine's data root: <see cref="SqliteRagParameters.DataRoot"/>
    /// when set, otherwise the shared <see cref="StorageOptions.DataRoot"/>.
    /// </summary>
    public SqliteRagPaths(IOptions<StorageOptions> storage, IOptions<SqliteRagParameters> parameters)
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
                $"No data root configured: set {SqliteRagParameters.SectionName}:{nameof(SqliteRagParameters.DataRoot)} " +
                $"or {StorageOptions.SectionName}:{nameof(StorageOptions.DataRoot)}.");
        }
    }

    private string UsersDir => Path.Combine(_dataRoot, "users");

    /// <summary>The RAG database <c>users/{key}/projects/{pid}/rag.db</c>.</summary>
    public string RagDb(string iss, string sub, string pid) =>
        Path.Combine(ProjectRoot(iss, sub, pid), "rag.db");

    /// <summary>
    /// Every <c>rag.db</c> under the user root - one per project - the RAG half of a
    /// whole-account delete. Enumerates the on-disk <c>projects/</c> directory so an orphaned
    /// project folder (a db with no registry row) is caught too.
    /// </summary>
    public IReadOnlyList<string> UserRagDatabaseFiles(string iss, string sub) =>
        RagDatabaseFilesUnder(Path.Combine(UsersDir, StorageKeys.UserKey(iss, sub)));

    /// <summary>As <see cref="UserRagDatabaseFiles"/> but addressed by folder key (admin path; F6-validated).</summary>
    public IReadOnlyList<string> UserRagDatabaseFilesByKey(string key)
    {
        StorageKeys.ValidateUserKey(key);
        return RagDatabaseFilesUnder(Path.Combine(UsersDir, key));
    }

    private static IReadOnlyList<string> RagDatabaseFilesUnder(string root)
    {
        var files = new List<string>();

        var projectsDir = Path.Combine(root, "projects");
        if (Directory.Exists(projectsDir))
        {
            foreach (var projectDir in Directory.EnumerateDirectories(projectsDir))
            {
                files.Add(Path.Combine(projectDir, "rag.db"));
            }
        }

        return files;
    }

    /// <summary>
    /// The project folder <c>{DataRoot}/users/{key}/projects/{pid}</c>. Throws
    /// <see cref="ArgumentException"/> if <paramref name="pid"/> is not a UUID or the literal
    /// <c>default</c>, or if the resolved path would escape the user root (defensive check).
    /// </summary>
    private string ProjectRoot(string iss, string sub, string pid)
    {
        StorageKeys.ValidatePid(pid);

        var root = Path.Combine(UsersDir, StorageKeys.UserKey(iss, sub));
        var projectRoot = Path.Combine(root, "projects", pid);

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
}
