using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gert.Database;

/// <summary>
/// One-call DI registration for the GENERIC database layer (tech-stack.md section
/// Architecture): the bound <see cref="DatabaseOptions"/>, the keyed-plugin
/// <see cref="DatabaseEngineFactory"/>, and the two provider ports resolved from the selected
/// engine. It registers no engine - the composition root adds the engine plugin it wants
/// (each <c>AddGertDatabase&lt;Impl&gt;</c>, e.g. <c>AddGertDatabaseSqlite</c>), and
/// <c>Gert:Database:Type</c> selects which engine builds the providers at runtime. The service
/// layer talks only to the ports (<see cref="IUserDatabaseProvider"/> /
/// <see cref="IChatDatabaseProvider"/>). The RAG index is its own capability
/// (<c>Gert.Rag</c> / <c>AddGertRag</c>), not a database concern.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the generic database engine selector + the two provider ports. Each provider is
    /// resolved from the engine the <see cref="DatabaseEngineFactory"/> selects for
    /// <c>Gert:Database:Type</c>; <c>TryAdd</c> so a host or test may override any port with a fake.
    /// </summary>
    public static IServiceCollection AddGertDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Bind + validate the engine selection at startup (fail at boot, not first use).
        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateOnStart();

        // The keyed-plugin selector, closed over IConfiguration (host-agnostic registration).
        services.TryAddSingleton(sp => new DatabaseEngineFactory(sp, configuration));

        // The two ports the service layer drives, each built by the selected engine. The engine
        // is resolved + cached once by the factory; the providers themselves are stateless over
        // bound options + open-per-use connections, so singletons.
        services.TryAddSingleton<IUserDatabaseProvider>(sp =>
            sp.GetRequiredService<DatabaseEngineFactory>().Engine().BuildUserDatabaseProvider());
        services.TryAddSingleton<IChatDatabaseProvider>(sp =>
            sp.GetRequiredService<DatabaseEngineFactory>().Engine().BuildChatDatabaseProvider());

        return services;
    }
}
