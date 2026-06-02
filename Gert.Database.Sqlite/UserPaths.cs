using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Gert.Database.Sqlite;

/// <summary>
/// Resolves the per-<c>(iss, sub)</c> user folder and its per-<c>pid</c> project
/// paths (storage-and-data.md § resolving paths). The folder key is
/// <c>sha256(iss + "\n" + sub)</c> lowercase hex (decisions §3) — fixed-length,
/// path-safe, and traversal-proof for any value the IdP emits.
///
/// <para>
/// <c>(iss, sub)</c> come only from the validated token; <c>EnsureProvisioned</c>
/// gates them <b>before</b> any of these methods run. The <c>pid</c> comes from
/// the request, so it is validated to a UUID or the literal <c>default</c> and is
/// only ever joined <b>under</b> the user root — it can select among this user's
/// projects but can never escape the user folder (cross-user IDOR is structurally
/// impossible). See storage-and-data.md § resolving paths and security F12.
/// </para>
/// </summary>
public sealed class UserPaths(IOptions<StorageOptions> options)
{
    /// <summary>The literal landing-project id, always present (storage-and-data.md § layout).</summary>
    public const string DefaultProjectId = "default";

    private readonly StorageOptions _options = options.Value;

    /// <summary>The configured <c>{DataRoot}/users</c> directory.</summary>
    public string UsersDir => Path.Combine(_options.DataRoot, "users");

    /// <summary>
    /// Folder key — <c>sha256(iss + "\n" + sub)</c> lowercase hex (decisions §3).
    /// </summary>
    public static string Key(string iss, string sub) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{iss}\n{sub}"))).ToLowerInvariant();

    // ---- user-level paths --------------------------------------------------

    /// <summary>The user folder root, <c>{DataRoot}/users/{key}</c>.</summary>
    public string Root(string iss, string sub) => Path.Combine(UsersDir, Key(iss, sub));

    /// <summary>The user identity binding file <c>meta.json</c>.</summary>
    public string MetaFile(string iss, string sub) => Path.Combine(Root(iss, sub), "meta.json");

    /// <summary>The user preferences file <c>settings.json</c>.</summary>
    public string SettingsFile(string iss, string sub) => Path.Combine(Root(iss, sub), "settings.json");

    /// <summary>The <c>projects/</c> directory under the user root.</summary>
    public string ProjectsDir(string iss, string sub) => Path.Combine(Root(iss, sub), "projects");

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
        // fully-resolved path stays under the user root — a value can never escape.
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

    /// <summary>The project config file <c>projects/{pid}/meta.json</c>.</summary>
    public string ProjectMeta(string iss, string sub, string pid) =>
        Path.Combine(ProjectRoot(iss, sub, pid), "meta.json");

    /// <summary>The conversations database <c>projects/{pid}/chat.db</c>.</summary>
    public string ChatDb(string iss, string sub, string pid) =>
        Path.Combine(ProjectRoot(iss, sub, pid), "chat.db");

    /// <summary>The RAG database <c>projects/{pid}/rag.db</c> (sqlite-vec + FTS5).</summary>
    public string RagDb(string iss, string sub, string pid) =>
        Path.Combine(ProjectRoot(iss, sub, pid), "rag.db");

    /// <summary>The uploaded-files directory <c>projects/{pid}/files/</c>.</summary>
    public string FilesDir(string iss, string sub, string pid) =>
        Path.Combine(ProjectRoot(iss, sub, pid), "files");

    /// <summary>The memory-entries directory <c>projects/{pid}/memory/</c>.</summary>
    public string MemoryDir(string iss, string sub, string pid) =>
        Path.Combine(ProjectRoot(iss, sub, pid), "memory");

    /// <summary>
    /// Reject any <paramref name="pid"/> that is not a UUID or the literal
    /// <c>default</c> (configuration.md §2.5). Rejecting the shape up front means
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
