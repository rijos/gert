using Gert.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gert.Storage.Local;

/// <summary>
/// One-call DI registration for the LOCAL-filesystem storage adapter
/// (dotnet-style-guide.md section 4: every adapter gets one <c>AddGertX</c> extension;
/// storage-and-data.md section lazy provisioning). The host (<c>Gert.Api</c>) calls this
/// alongside <c>AddGertDatabaseSqlite</c>; an S3/Azure-Blob backend swaps in by calling its
/// own <c>AddGertStorage*</c> instead. Everything is <c>TryAdd</c>, so a host or test may
/// override a seam with a plain <c>Add</c>/<c>Replace</c>.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the local storage backend: bound <see cref="StorageOptions"/> and the
    /// <see cref="IObjectStore"/> local-filesystem backend. <see cref="StorageOptions"/>
    /// is bound here too (the same "Storage" data-root the SQLite db paths use) so this
    /// adapter is self-contained.
    /// </summary>
    public static IServiceCollection AddGertStorageLocal(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Bind the data-root options (idempotent with AddGertDatabaseSqlite, which also needs
        // them for the per-user db paths): fail at startup, not first use.
        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.SectionName))
            .ValidateOnStart();

        // THE storage-backend seam: every non-database byte under a user tree (uploads,
        // memory bodies) flows through IObjectStore - blob CRUD plus the scope/prefix
        // deletes and the admin footprint scan. The local backend writes under
        // {DataRoot}/users; an S3/Azure-Blob backend is a drop-in: one IObjectStore impl,
        // one DI registration overriding this one. The user/project DELETE orchestration
        // (DB half + blob half) lives in the service layer, not here.
        services.TryAddSingleton<IObjectStore, LocalObjectStore>();

        // The deletion journal: the durable write-ahead record that makes erasing a user
        // crash-consistent across the independent stores (markers under {DataRoot}/.pending-
        // deletions). Same storage domain as the blobs, so a backend swap carries it along.
        services.TryAddSingleton<IDeletionJournal, LocalDeletionJournal>();

        return services;
    }
}
