using Gert.Database;
using Gert.Database.Sqlite;
using Gert.Model.Projects;
using Gert.Rag;
using Gert.Rag.Sqlite;
using Gert.Storage;
using Gert.Storage.Local;
using Microsoft.Extensions.Options;

namespace Gert.Testing;

/// <summary>
/// Shared helpers to spin the split SQLite database providers (user/chat/rag), the
/// <see cref="LocalObjectStore"/> backend, and <see cref="SqliteDatabasePaths"/> over a
/// throwaway <see cref="TempDataRoot"/>. <see cref="ProviderFor"/> returns a
/// <see cref="TestDatabases"/> facade that replicates the request-edge provisioner
/// (username + default project). Lives in Gert.Testing so both the database-engine and
/// the RAG-engine SQLite test projects share one fixture.
/// </summary>
public static class ProviderFixture
{
    /// <summary>The issuer string the suite mints identities under.</summary>
    public const string ExpectedIssuer = "https://id.test.local";

    public static StorageOptions OptionsFor(TempDataRoot root) => new()
    {
        DataRoot = root.Path,
    };

    public static SqliteConnectionFactory FactoryFor(TempDataRoot root) =>
        new();

    public static SqliteRagConnectionFactory RagFactoryFor(TempDataRoot root) =>
        new(Options.Create(new SqliteRagParameters()));

    public static LocalObjectStore ObjectsFor(TempDataRoot root) =>
        new(Options.Create(OptionsFor(root)));

    public static LocalDeletionJournal JournalFor(TempDataRoot root) =>
        new(Options.Create(OptionsFor(root)));

    public static SqliteDatabasePaths PathsFor(TempDataRoot root) =>
        new(Options.Create(OptionsFor(root)), Options.Create(new SqliteDatabaseParameters()));

    public static SqliteRagPaths RagPathsFor(TempDataRoot root) =>
        new(Options.Create(OptionsFor(root)), Options.Create(new SqliteRagParameters()));

    public static IUserDatabaseProvider UserProviderFor(TempDataRoot root) =>
        new SqliteUserDatabaseProvider(PathsFor(root), FactoryFor(root), TimeProvider.System);

    public static IChatDatabaseProvider ChatProviderFor(TempDataRoot root) =>
        new SqliteChatDatabaseProvider(PathsFor(root), FactoryFor(root));

    public static IRagIndexProvider RagProviderFor(TempDataRoot root) =>
        new SqliteRagIndexProvider(RagPathsFor(root), RagFactoryFor(root));

    public static TestDatabases ProviderFor(TempDataRoot root) => new(OptionsFor(root));

    /// <summary>
    /// Test facade over the three split providers: the old open/ensure surface plus
    /// the provisioning the hosts run at their edge (seed the username + default
    /// project, materialise a project's chat.db/rag.db).
    /// </summary>
    public sealed class TestDatabases
    {
        public IUserDatabaseProvider Users { get; }
        public IChatDatabaseProvider Chat { get; }
        public IRagIndexProvider Rag { get; }

        public TestDatabases(StorageOptions options)
        {
            var opt = Options.Create(options);
            var dbParams = Options.Create(new SqliteDatabaseParameters());
            var ragParams = Options.Create(new SqliteRagParameters());
            var paths = new SqliteDatabasePaths(opt, dbParams);
            var ragPaths = new SqliteRagPaths(opt, ragParams);
            var factory = new SqliteConnectionFactory();
            var ragFactory = new SqliteRagConnectionFactory(ragParams);
            Users = new SqliteUserDatabaseProvider(paths, factory, TimeProvider.System);
            Chat = new SqliteChatDatabaseProvider(paths, factory);
            Rag = new SqliteRagIndexProvider(ragPaths, ragFactory);
        }

        public Task<IChatRepository> OpenChatAsync(string iss, string sub, string pid, CancellationToken ct = default) =>
            Chat.OpenAsync(iss, sub, pid, ct);

        public Task<IRagStore> OpenRagAsync(string iss, string sub, string pid, CancellationToken ct = default) =>
            Rag.OpenAsync(iss, sub, pid, ct);

        public async Task EnsureProvisionedAsync(string iss, string sub, CancellationToken ct = default)
        {
            await using (var repo = await Users.OpenAsync(iss, sub, ct).ConfigureAwait(false))
            {
                await repo.SetUsernameAsync(sub, ct).ConfigureAwait(false);
            }

            await EnsureProjectAsync(iss, sub, StorageKeys.DefaultProjectId, ct).ConfigureAwait(false);
        }

        public async Task EnsureProjectAsync(string iss, string sub, string pid, CancellationToken ct = default)
        {
            await using (var repo = await Users.OpenAsync(iss, sub, ct).ConfigureAwait(false))
            {
                if (await repo.GetProjectAsync(pid, ct).ConfigureAwait(false) is null)
                {
                    var now = DateTimeOffset.UtcNow;
                    await repo.SaveProjectAsync(
                        new ProjectMeta
                        {
                            Id = pid,
                            Name = pid == StorageKeys.DefaultProjectId ? "Default" : pid,
                            CreatedAt = now,
                            UpdatedAt = now,
                        },
                        ct).ConfigureAwait(false);
                }
            }

            // Materialise the project's databases (the old EnsureProject migrated both).
            await (await Chat.OpenAsync(iss, sub, pid, ct).ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
            await (await Rag.OpenAsync(iss, sub, pid, ct).ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
        }
    }
}
