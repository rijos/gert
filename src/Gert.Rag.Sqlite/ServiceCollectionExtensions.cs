using Gert.Rag;
using Gert.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gert.Rag.Sqlite;

/// <summary>
/// DI registration for the <c>Sqlite</c> RAG-engine IMPLEMENTATION plugin
/// (dotnet-style-guide.md section 4; tech-stack.md section Architecture). The composition root
/// calls the generic <c>AddGertRag</c> (the engine selector + the index provider port) and then
/// this method to make the sqlite-vec engine available; configuration selects it via
/// <c>Gert:Rag:Type = Sqlite</c> (the default). This registers the bound
/// <see cref="StorageOptions"/> / <see cref="SqliteRagParameters"/>, the
/// <see cref="SqliteRagConnectionFactory"/>, and the keyed <see cref="SqliteRagEngineBuilder"/>;
/// the generic <see cref="RagEngineFactory"/> dispatches to it by Type with no central switch.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Register the SQLite RAG engine plugin: bound options + the connection factory + the keyed builder.</summary>
    public static IServiceCollection AddGertRagSqlite(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // The shared "Storage" data-root (fallback) + the engine-private parameters
        // (Gert:Rag:Parameters): an optional DataRoot override and the sqlite-vec extension
        // path. Fail at startup, not first use.
        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.SectionName))
            .ValidateOnStart();
        services.AddOptions<SqliteRagParameters>()
            .Bind(configuration.GetSection(SqliteRagParameters.SectionName))
            .ValidateOnStart();

        // The RAG connection factory: open(+vec)/migrate + drop-pools/unlink. Singleton:
        // stateless over bound options, open-per-use connections.
        services.TryAddSingleton<SqliteRagConnectionFactory>();

        // Self-register the keyed engine plugin; the generic RagEngineFactory builds the
        // rag.db index provider from it when Gert:Rag:Type is Sqlite.
        services.AddKeyedSingleton<IRagEngineBuilder, SqliteRagEngineBuilder>(
            RagEngineFactory.NormalizeType("Sqlite"));

        return services;
    }
}
