using Gert.Database.Sqlite;
using Gert.External;
using Gert.Storage;
using Gert.Service;
using Gert.Database;
using Gert.Service.Storage;
using Gert.Service.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gert.Console;

/// <summary>
/// Builds the Console host's service graph — the non-HTTP mirror of
/// <c>Gert.Api/Program.cs</c> (tech-stack.md § Architecture). The Console drives
/// the <b>same</b> services directly: a single fixed user
/// (<see cref="LocalUserContext"/>, tools = <c>"*"</c>), ingestion run
/// <b>inline</b> (the default <c>InlineIngestionQueue</c>, no Channel worker /
/// BackgroundService), and <b>no</b> <c>Gert.Authentication</c> reference — the
/// "Console must not need the API" guarantee is structural.
/// </summary>
public static class ConsoleHostBuilder
{
    /// <summary>
    /// Register the full Console service graph into <paramref name="services"/>:
    /// <see cref="ServiceCollectionExtensions.AddGertServices"/>, the storage seam
    /// (SQLite provider / object store / user store), the
    /// <see cref="LocalUserContext"/>, and the real external adapters via
    /// <c>AddGertExternal</c>. Tests call this and then <c>Replace</c> the external
    /// ports with the <c>Gert.Testing</c> fakes.
    /// <para>
    /// The inline ingestion queue is the default registered by
    /// <see cref="ServiceCollectionExtensions.AddGertServices"/> — the Console does
    /// <b>not</b> add the Channel worker, so ingestion runs synchronously on the
    /// calling thread (a document is <c>ready</c>/<c>failed</c> by the time
    /// <c>UploadAsync</c> returns).
    /// </para>
    /// </summary>
    public static IServiceCollection AddGertConsole(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Host-agnostic service layer (incl. the inline IIngestionQueue default).
        services.AddGertServices();

        // Storage seam (storage-and-data.md § lazy provisioning) — identical to the
        // Api's non-HTTP wiring.
        services.Configure<StorageOptions>(
            configuration.GetSection(StorageOptions.SectionName));
        services.Configure<SqliteVecOptions>(
            configuration.GetSection(SqliteVecOptions.SectionName));
        services.Configure<ToolOptions>(
            configuration.GetSection(ToolOptions.SectionName));
        services.TryAddSingleton<SqliteConnectionFactory>();
        services.TryAddSingleton<IUserDatabaseProvider, SqliteUserDatabaseProvider>();
        services.TryAddSingleton<IChatDatabaseProvider, SqliteChatDatabaseProvider>();
        services.TryAddSingleton<IRagDatabaseProvider, SqliteRagDatabaseProvider>();
        services.TryAddSingleton<IDatabaseHandleReleaser, SqliteHandleReleaser>();
        services.TryAddSingleton<IObjectStore, LocalObjectStore>();
        services.TryAddSingleton<IUserStore, ObjectStoreUserStore>();

        // The single fixed local user — tools = "*" via ToolRegistry.AllIds. Singleton:
        // the identity never changes for the life of the process.
        services.TryAddSingleton<IUserContext, LocalUserContext>();

        // Real outside-world adapters (vLLM / SearXNG / gVisor sandbox + isolated
        // extractor). Tests Replace these four ports with the in-process fakes.
        services.AddGertExternal(configuration);

        return services;
    }
}
