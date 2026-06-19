using Gert.Database;
using Gert.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gert.Database.Sqlite;

/// <summary>
/// DI registration for the <c>Sqlite</c> database-engine implementation plugin
/// (dotnet-style-guide.md section 4; tech-stack.md section Architecture). The generic
/// <c>DatabaseEngineFactory</c> dispatches to it by Type with no central switch; config selects
/// it via <c>Gert:Database:Type = Sqlite</c> (the default). Each provider owns destroying its own
/// database files (drop pooled handles + unlink); the storage layer never reaches into the engine.
/// The RAG index and the <c>IObjectStore</c> backend are separate capabilities.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGertDatabaseSqlite(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Bind the shared "Storage" data-root via the section 4 idiom (dotnet-style-guide.md):
        // ValidateOnStart fails at startup, not first use. No data annotations to validate.
        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.SectionName))
            .ValidateOnStart();

        // Engine-private parameters (Gert:Database:Parameters) - optional DataRoot override;
        // unset means fall back to the shared Storage:DataRoot.
        services.AddOptions<SqliteDatabaseParameters>()
            .Bind(configuration.GetSection(SqliteDatabaseParameters.SectionName))
            .ValidateOnStart();

        // SqliteUserDatabaseProvider stamps row timestamps via TimeProvider
        // (dotnet-style-guide.md section 5); TryAdd keeps the host/AddGertServices
        // registration (and a test fake) authoritative.
        services.TryAddSingleton(TimeProvider.System);

        // The connection factory does the open + migrate-on-open (and the drop-pools + unlink
        // for deletes) for user.db + chat.db. Singleton: stateless over bound options,
        // open-per-use connections.
        services.TryAddSingleton<SqliteConnectionFactory>();

        // Self-register the keyed engine plugin; the generic DatabaseEngineFactory builds the
        // user.db / chat.db providers from it when Gert:Database:Type is Sqlite.
        services.AddKeyedSingleton<IDatabaseEngineBuilder, SqliteDatabaseEngineBuilder>(
            DatabaseEngineFactory.NormalizeType("Sqlite"));

        return services;
    }
}
