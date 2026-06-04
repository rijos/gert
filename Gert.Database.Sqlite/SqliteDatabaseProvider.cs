using System.Text.RegularExpressions;
using Gert.Database;
using Gert.Service.Database;
using Gert.Service.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Gert.Database.Sqlite;

/// <summary>
/// <see cref="IDatabaseProvider"/> over <c>Microsoft.Data.Sqlite</c>
/// (storage-and-data.md § connection management / lazy provisioning). Connections
/// are open-per-use: every one is opened with WAL, a 5s busy timeout, and
/// foreign keys ON, used, then disposed by the caller (the returned repository).
///
/// <para>
/// Provisioning is <b>fail-closed</b> (security F12 / decisions §3): the identity
/// is validated <i>before any path is derived or directory created</i>, so a
/// bad/unexpected identity never yields a folder. Past that gate the validated JWT
/// is trusted — the folder key derives from the token and nothing else.
/// <c>meta.json</c> is a descriptive sidecar (username for the admin scan,
/// <c>schema_version</c> for migrations) rewritten when missing or unreadable.
/// </para>
///
/// <para>
/// Both <c>chat.db</c> and <c>rag.db</c> are provisioned/migrated/opened here.
/// <c>rag.db</c> connections additionally load the native <b>sqlite-vec</b>
/// extension (<see cref="OpenRagConnectionAsync"/>) so the <c>vec0</c> / FTS5
/// virtual tables in the rag migration family can be created and queried.
/// </para>
/// </summary>
public sealed partial class SqliteDatabaseProvider : IDatabaseProvider
{
    private readonly StorageOptions _options;
    private readonly SqliteVecOptions _vecOptions;
    private readonly UserPaths _paths;
    private readonly IUserStore _files;

    /// <summary>
    /// Create the provider with bound <see cref="StorageOptions"/> /
    /// <see cref="SqliteVecOptions"/> and the file layer (<see cref="IUserStore"/>)
    /// that owns all config-file I/O (<c>meta.json</c>, <c>settings.json</c>).
    /// </summary>
    public SqliteDatabaseProvider(
        IOptions<StorageOptions> options,
        IOptions<SqliteVecOptions> vecOptions,
        IUserStore files)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(vecOptions);
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _options = options.Value;
        _vecOptions = vecOptions.Value;

        if (string.IsNullOrWhiteSpace(_options.DataRoot))
        {
            throw new InvalidOperationException($"{nameof(StorageOptions)}.{nameof(StorageOptions.DataRoot)} must be configured.");
        }

        if (string.IsNullOrWhiteSpace(_options.ExpectedIssuer))
        {
            throw new InvalidOperationException($"{nameof(StorageOptions)}.{nameof(StorageOptions.ExpectedIssuer)} must be configured.");
        }

        _paths = new UserPaths(options);
    }

    // Bounded charset/length for sub (storage-and-data.md step 0; decisions §3).
    [GeneratedRegex(@"^[A-Za-z0-9._:\-]{1,128}$")]
    private static partial Regex SubPattern();

    /// <inheritdoc />
    public async Task EnsureProvisionedAsync(
        string iss,
        string sub,
        CancellationToken cancellationToken = default)
    {
        await EnsureUserRootAsync(iss, sub, cancellationToken).ConfigureAwait(false);

        // The landing project is always present.
        await EnsureProjectAsync(iss, sub, UserPaths.DefaultProjectId, cancellationToken).ConfigureAwait(false);
    }

    // Ensure the user root dir + descriptive meta.json + settings.json exist.
    // Does NOT create any project, so EnsureProjectAsync can call it without recursing
    // into EnsureProvisionedAsync. Idempotent + fail-closed.
    private async Task EnsureUserRootAsync(
        string iss,
        string sub,
        CancellationToken cancellationToken = default)
    {
        // ---- step 0: validate the identity BEFORE touching disk (fail-closed) ----
        // No path is derived and no directory created until these pass.
        ValidateIdentity(iss, sub);

        // All config-file I/O (root dir, the meta.json sidecar — healed when
        // unreadable — and default settings.json) belongs to the file layer; this
        // provider only provisions databases.
        await _files.EnsureUserFilesAsync(iss, sub, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task EnsureProjectAsync(
        string iss,
        string sub,
        string pid,
        CancellationToken cancellationToken = default)
    {
        // A project always belongs to a provisioned user: ensure the user root +
        // meta.json sidecar exist FIRST (idempotent; validates the identity before
        // touching disk). This guarantees a direct EnsureProjectAsync caller (e.g.
        // ProjectService.Create) never materialises the user dir without its
        // metadata. EnsureUserRootAsync creates no project → no recursion.
        await EnsureUserRootAsync(iss, sub, cancellationToken).ConfigureAwait(false);
        UserPaths.ValidatePid(pid);

        // The on-disk project skeleton (dirs + meta.json) is the file layer's job;
        // this provider only provisions/migrates the databases below.
        await _files.EnsureProjectFilesAsync(iss, sub, pid, cancellationToken).ConfigureAwait(false);

        // chat.db: open + apply chat migrations by PRAGMA user_version.
        await using (var chat = await OpenConnectionAsync(_paths.ChatDb(iss, sub, pid), cancellationToken)
            .ConfigureAwait(false))
        {
            await SqliteMigrationRunner.ApplyAsync(chat, "chat", cancellationToken).ConfigureAwait(false);
        }

        // rag.db: open with the sqlite-vec extension loaded, then apply the "rag"
        // migration family (vec0 + fts5 virtual tables need the extension present).
        await using var rag = await OpenRagConnectionAsync(_paths.RagDb(iss, sub, pid), cancellationToken)
            .ConfigureAwait(false);
        await SqliteMigrationRunner.ApplyAsync(rag, "rag", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IChatRepository> OpenChatAsync(
        string iss,
        string sub,
        string pid,
        CancellationToken cancellationToken = default)
    {
        // Ensure the user (gate + sidecars + default project) then the
        // requested project, per the interface contract ("provisioning is ensured
        // first"). Idempotent, so the common already-provisioned path is cheap.
        await EnsureProvisionedAsync(iss, sub, cancellationToken).ConfigureAwait(false);
        await EnsureProjectAsync(iss, sub, pid, cancellationToken).ConfigureAwait(false);

        var connection = await OpenConnectionAsync(_paths.ChatDb(iss, sub, pid), cancellationToken)
            .ConfigureAwait(false);
        return new SqliteChatRepository(connection);
    }

    /// <inheritdoc />
    public async Task<IRagRepository> OpenRagAsync(
        string iss,
        string sub,
        string pid,
        CancellationToken cancellationToken = default)
    {
        // Ensure the user + project (incl. rag.db migration), per the interface
        // contract. Idempotent, so the already-provisioned path is cheap.
        await EnsureProvisionedAsync(iss, sub, cancellationToken).ConfigureAwait(false);
        await EnsureProjectAsync(iss, sub, pid, cancellationToken).ConfigureAwait(false);

        var connection = await OpenRagConnectionAsync(_paths.RagDb(iss, sub, pid), cancellationToken)
            .ConfigureAwait(false);
        return new SqliteRagRepository(connection);
    }

    /// <summary>The folder key for a validated identity (exposed for diagnostics/tests).</summary>
    public string KeyFor(string iss, string sub) => UserPaths.Key(iss, sub);

    // ---- gate + binding ----------------------------------------------------

    /// <summary>
    /// Fail-closed identity gate (storage-and-data.md step 0; security F12). Runs
    /// before any path derive or mkdir. Throws on failure — no folder is created
    /// for an unvalidated identity.
    /// </summary>
    private void ValidateIdentity(string iss, string sub)
    {
        if (string.IsNullOrEmpty(iss) || !string.Equals(iss, _options.ExpectedIssuer, StringComparison.Ordinal))
        {
            throw new UnauthorizedIdentityException(
                $"Issuer '{iss}' is not the configured authority.");
        }

        if (string.IsNullOrEmpty(sub) || !SubPattern().IsMatch(sub))
        {
            throw new UnauthorizedIdentityException(
                "Subject is missing or outside the permitted charset/length (^[A-Za-z0-9._:\\-]{1,128}$).");
        }
    }

    // ---- connections -------------------------------------------------------

    /// <summary>
    /// Open a connection to <paramref name="dbPath"/> with the project's pragmas
    /// (storage-and-data.md § connection management): WAL, busy_timeout=5000,
    /// foreign_keys=ON. Open-per-use; the caller disposes.
    /// </summary>
    private static async Task<SqliteConnection> OpenConnectionAsync(
        string dbPath,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var pragma = connection.CreateCommand();
            pragma.CommandText =
                "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Open a <c>rag.db</c> connection: the same pragmas as
    /// <see cref="OpenConnectionAsync"/>, then enable + load the native
    /// <b>sqlite-vec</b> extension so the <c>vec0</c> / FTS5 virtual tables are
    /// usable (chat-and-tools.md § "Loading sqlite-vec in .NET").
    /// </summary>
    private async Task<SqliteConnection> OpenRagConnectionAsync(
        string dbPath,
        CancellationToken cancellationToken)
    {
        var connection = await OpenConnectionAsync(dbPath, cancellationToken).ConfigureAwait(false);
        try
        {
            connection.EnableExtensions(true);
            connection.LoadExtension(ResolveVecExtensionPath());
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// The configured <see cref="SqliteVecOptions.VecExtensionPath"/>, or
    /// <c>vec0.so</c> beside the running assembly (the csproj copies the vendored
    /// extension into every consumer's output).
    /// </summary>
    private string ResolveVecExtensionPath() =>
        string.IsNullOrWhiteSpace(_vecOptions.VecExtensionPath)
            ? Path.Combine(AppContext.BaseDirectory, "vec0.so")
            : _vecOptions.VecExtensionPath;
}
