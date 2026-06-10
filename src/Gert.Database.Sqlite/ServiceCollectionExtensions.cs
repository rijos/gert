using Gert.Service.Storage;
using Gert.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gert.Database.Sqlite;

/// <summary>
/// One-call DI registration for the SQLite + local-filesystem storage seam
/// (dotnet-style-guide.md §4: every adapter gets one <c>AddGertX</c> extension;
/// storage-and-data.md § lazy provisioning). Both hosts (<c>Gert.Api</c>,
/// <c>Gert.Console</c>) call this in place of the previously copy-pasted
/// registration block. Everything is <c>TryAdd</c>, so a host or test may
/// override any seam (e.g. swap <see cref="IObjectStore"/> for an S3 backend)
/// with a plain <c>Add</c>/<c>Replace</c>.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the storage/database seam: bound <see cref="StorageOptions"/> /
    /// <see cref="SqliteVecOptions"/>, the shared <see cref="SqliteConnectionFactory"/>,
    /// the three self-provisioning database providers, the handle releaser, and the
    /// local object/user stores.
    /// </summary>
    public static IServiceCollection AddGertSqliteStorage(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Options bind via the §4 idiom (dotnet-style-guide.md): fail at startup,
        // not first use. Neither type carries data annotations, so no
        // ValidateDataAnnotations. Both bind the same "Storage" section — the
        // binder ignores keys it doesn't own.
        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.SectionName))
            .ValidateOnStart();
        services.AddOptions<SqliteVecOptions>()
            .Bind(configuration.GetSection(SqliteVecOptions.SectionName))
            .ValidateOnStart();

        // SqliteUserDatabaseProvider stamps row timestamps via TimeProvider
        // (dotnet-style-guide.md §5); TryAdd keeps the host/AddGertServices
        // registration (and a test fake) authoritative.
        services.TryAddSingleton(TimeProvider.System);

        // Three self-provisioning database seams (no shared "ensure", no memoised
        // cache): user.db (username, settings, project registry) + per-project
        // chat.db / rag.db. The shared connection factory does the open +
        // migrate-on-open for all of them. Singletons: stateless over bound
        // options, open-per-use connections.
        services.TryAddSingleton<SqliteConnectionFactory>();
        services.TryAddSingleton<IUserDatabaseProvider, SqliteUserDatabaseProvider>();
        services.TryAddSingleton<IChatDatabaseProvider, SqliteChatDatabaseProvider>();
        services.TryAddSingleton<IRagDatabaseProvider, SqliteRagDatabaseProvider>();

        // Lets the storage backend drop SQLite's pooled chat.db/rag.db handles before
        // a local whole-tree delete; a server-backed adapter (e.g. Postgres) registers
        // a no-op.
        services.TryAddSingleton<IDatabaseHandleReleaser, SqliteHandleReleaser>();

        // THE storage-backend seam: every non-database byte under a user tree
        // (uploads, memory bodies, config sidecars) flows through IObjectStore. The
        // local backend writes under {DataRoot}/users; an S3/Azure-Blob backend is a
        // drop-in: one IObjectStore impl, one DI registration overriding this one.
        services.TryAddSingleton<IObjectStore, LocalObjectStore>();

        // Coarse blob lifecycle seam (scope deletes, the admin footprint scan;
        // structured config lives in user.db — storage-and-data.md § "No JSON
        // sidecars") — backend-agnostic: everything goes through IObjectStore.
        services.TryAddSingleton<IUserStore, ObjectStoreUserStore>();

        return services;
    }
}
