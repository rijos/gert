using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gert.Model.Projects;
using Gert.Service.Database;
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
/// is validated <i>before any path is derived or directory created</i>, and an
/// existing folder's <c>meta.json</c> <c>(iss, sub)</c> binding is re-asserted
/// against the token on every touch. A bad/unexpected identity never yields a
/// folder; a binding mismatch refuses the request.
/// </para>
///
/// <para>
/// SCOPE NOTE (U4a): only <c>chat.db</c> is provisioned/migrated/opened here.
/// rag.db needs the sqlite-vec native extension (U4b), so rag provisioning is not
/// applied and <see cref="OpenRagAsync"/> returns the stub
/// <see cref="SqliteRagRepository"/> (whose members throw with a TODO U4b note).
/// </para>
/// </summary>
public sealed partial class SqliteDatabaseProvider : IDatabaseProvider
{
    private const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly StorageOptions _options;
    private readonly UserPaths _paths;

    /// <summary>Create the provider with bound <see cref="StorageOptions"/>.</summary>
    public SqliteDatabaseProvider(IOptions<StorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;

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
        // ---- step 0: validate the identity BEFORE touching disk (fail-closed) ----
        // No path is derived and no directory created until these pass.
        ValidateIdentity(iss, sub);

        // Only now is it safe to derive a path from the (accepted) identity.
        var root = _paths.Root(iss, sub);
        var metaFile = _paths.MetaFile(iss, sub);

        if (Directory.Exists(root))
        {
            // ---- step 1a: existing folder -> verify the identity binding ----
            await VerifyBindingAsync(metaFile, iss, sub, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // ---- step 1b: fresh folder -> create it and write the binding ----
            Directory.CreateDirectory(root);

            var meta = new UserMeta
            {
                Iss = iss,
                Sub = sub,
                Username = sub, // username is refreshed from the token elsewhere; sub is the safe default.
                CreatedAt = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                SchemaVersion = SchemaVersion,
            };
            await WriteJsonAsync(metaFile, meta, cancellationToken).ConfigureAwait(false);
        }

        // settings.json (defaults) — written only when absent so user edits survive.
        var settingsFile = _paths.SettingsFile(iss, sub);
        if (!File.Exists(settingsFile))
        {
            await WriteJsonAsync(settingsFile, new UserSettings(), cancellationToken).ConfigureAwait(false);
        }

        // The landing project is always present.
        await EnsureProjectAsync(iss, sub, UserPaths.DefaultProjectId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task EnsureProjectAsync(
        string iss,
        string sub,
        string pid,
        CancellationToken cancellationToken = default)
    {
        // The user gate must have run; re-validate the identity defensively so a
        // project is never materialised for an unvalidated/mismatched identity.
        ValidateIdentity(iss, sub);
        UserPaths.ValidatePid(pid);

        var metaFile = _paths.MetaFile(iss, sub);
        if (File.Exists(metaFile))
        {
            await VerifyBindingAsync(metaFile, iss, sub, cancellationToken).ConfigureAwait(false);
        }

        var projectRoot = _paths.ProjectRoot(iss, sub, pid);
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(_paths.FilesDir(iss, sub, pid));
        Directory.CreateDirectory(_paths.MemoryDir(iss, sub, pid));

        var projectMetaFile = _paths.ProjectMeta(iss, sub, pid);
        if (!File.Exists(projectMetaFile))
        {
            var now = DateTimeOffset.UtcNow;
            var projectMeta = new ProjectMeta
            {
                Id = pid,
                Name = pid == UserPaths.DefaultProjectId ? "Default" : pid,
                CreatedAt = now,
                UpdatedAt = now,
            };
            await WriteJsonAsync(projectMetaFile, projectMeta, cancellationToken).ConfigureAwait(false);
        }

        // chat.db: open + apply chat migrations by PRAGMA user_version.
        await using var connection = await OpenConnectionAsync(_paths.ChatDb(iss, sub, pid), cancellationToken)
            .ConfigureAwait(false);
        await SqliteMigrationRunner.ApplyAsync(connection, "chat", cancellationToken).ConfigureAwait(false);

        // TODO U4b: open rag.db here and apply the "rag" migration family once the
        // sqlite-vec native extension is loaded. Not done in U4a (scope note).
    }

    /// <inheritdoc />
    public async Task<IChatRepository> OpenChatAsync(
        string iss,
        string sub,
        string pid,
        CancellationToken cancellationToken = default)
    {
        // Ensure the user (gate + binding + settings + default project) then the
        // requested project, per the interface contract ("provisioning is ensured
        // first"). Idempotent, so the common already-provisioned path is cheap.
        await EnsureProvisionedAsync(iss, sub, cancellationToken).ConfigureAwait(false);
        await EnsureProjectAsync(iss, sub, pid, cancellationToken).ConfigureAwait(false);

        var connection = await OpenConnectionAsync(_paths.ChatDb(iss, sub, pid), cancellationToken)
            .ConfigureAwait(false);
        return new SqliteChatRepository(connection);
    }

    /// <inheritdoc />
    public Task<IRagRepository> OpenRagAsync(
        string iss,
        string sub,
        string pid,
        CancellationToken cancellationToken = default)
    {
        // TODO U4b: requires sqlite-vec native extension. For now return the stub so
        // the project compiles; provisioning does not create/open rag.db (scope note).
        ValidateIdentity(iss, sub);
        UserPaths.ValidatePid(pid);
        return Task.FromResult<IRagRepository>(new SqliteRagRepository());
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

    /// <summary>
    /// Identity binding check (security F12): the existing folder's
    /// <c>meta.json</c> <c>(iss, sub)</c> must equal the token's, else refuse.
    /// </summary>
    private static async Task VerifyBindingAsync(
        string metaFile,
        string iss,
        string sub,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(metaFile))
        {
            // A folder without its binding is corrupt/unsafe — never serve it.
            throw new IdentityBindingException(
                $"User folder is missing its identity binding ('{metaFile}').");
        }

        UserMeta? meta;
        await using (var stream = File.OpenRead(metaFile))
        {
            meta = await JsonSerializer.DeserializeAsync<UserMeta>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        if (meta is null ||
            !string.Equals(meta.Iss, iss, StringComparison.Ordinal) ||
            !string.Equals(meta.Sub, sub, StringComparison.Ordinal))
        {
            throw new IdentityBindingException(
                "Identity binding mismatch: the folder's recorded (iss, sub) does not match the token.");
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

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
    }
}
