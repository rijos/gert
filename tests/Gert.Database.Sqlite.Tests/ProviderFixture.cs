using Gert.Database;
using Gert.Model.Projects;
using Gert.Service.Storage;
using Gert.Storage;
using Gert.Testing;
using Microsoft.Extensions.Options;

namespace Gert.Database.Sqlite.Tests;

/// <summary>
/// Shared helpers to spin the split SQLite database providers (user/chat/rag), the
/// <see cref="LocalObjectStore"/> backend + <see cref="ObjectStoreUserStore"/> blob
/// layer, and <see cref="SqliteDatabasePaths"/> over a throwaway
/// <see cref="TempDataRoot"/>. <see cref="ProviderFor"/> returns a small
/// <see cref="TestDatabases"/> facade that keeps the legible open/ensure surface and
/// replicates the request-edge provisioner (username + default project).
/// </summary>
internal static class ProviderFixture
{
    /// <summary>The issuer string the suite mints identities under.</summary>
    public const string ExpectedIssuer = "https://id.test.local";

    public static StorageOptions OptionsFor(TempDataRoot root) => new()
    {
        DataRoot = root.Path,
    };

    public static SqliteConnectionFactory FactoryFor(TempDataRoot root) =>
        new(Options.Create(new SqliteVecOptions()));

    public static LocalObjectStore ObjectsFor(TempDataRoot root) =>
        new(Options.Create(OptionsFor(root)), new SqliteHandleReleaser());

    public static ObjectStoreUserStore StoreFor(TempDataRoot root) =>
        new(ObjectsFor(root));

    public static SqliteDatabasePaths PathsFor(TempDataRoot root) =>
        new(Options.Create(OptionsFor(root)));

    public static IUserDatabaseProvider UserProviderFor(TempDataRoot root) =>
        new SqliteUserDatabaseProvider(Options.Create(OptionsFor(root)), FactoryFor(root), TimeProvider.System);

    public static IChatDatabaseProvider ChatProviderFor(TempDataRoot root) =>
        new SqliteChatDatabaseProvider(Options.Create(OptionsFor(root)), FactoryFor(root));

    public static IRagDatabaseProvider RagProviderFor(TempDataRoot root) =>
        new SqliteRagDatabaseProvider(Options.Create(OptionsFor(root)), FactoryFor(root));

    public static TestDatabases ProviderFor(TempDataRoot root) => new(OptionsFor(root));

    /// <summary>
    /// Test facade over the three split providers: the old open/ensure surface plus
    /// the provisioning the hosts run at their edge (seed the username + default
    /// project, materialise a project's chat.db/rag.db).
    /// </summary>
    internal sealed class TestDatabases
    {
        public IUserDatabaseProvider Users { get; }
        public IChatDatabaseProvider Chat { get; }
        public IRagDatabaseProvider Rag { get; }

        public TestDatabases(StorageOptions options)
        {
            var opt = Options.Create(options);
            var factory = new SqliteConnectionFactory(Options.Create(new SqliteVecOptions()));
            Users = new SqliteUserDatabaseProvider(opt, factory, TimeProvider.System);
            Chat = new SqliteChatDatabaseProvider(opt, factory);
            Rag = new SqliteRagDatabaseProvider(opt, factory);
        }

        public Task<IChatRepository> OpenChatAsync(string iss, string sub, string pid, CancellationToken ct = default) =>
            Chat.OpenAsync(iss, sub, pid, ct);

        public Task<IRagRepository> OpenRagAsync(string iss, string sub, string pid, CancellationToken ct = default) =>
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
