using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gert.Rag;

/// <summary>
/// One-call DI registration for the GENERIC RAG layer (tech-stack.md section Architecture):
/// the bound <see cref="RagOptions"/> (the <c>Gert:Rag:Type</c> engine selection), the
/// keyed-plugin <see cref="RagEngineFactory"/>, and the <see cref="IRagIndexProvider"/> port
/// resolved from the selected engine. It registers no engine - the composition root adds the
/// engine plugin it wants available (each <c>AddGertRag&lt;Impl&gt;</c>, e.g.
/// <c>AddGertRagSqlite</c>), and configuration selects which engine builds the index at runtime.
/// The service layer keeps talking only to the port (<see cref="IRagIndexProvider"/>).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the generic RAG engine selector + the index provider port. The provider is
    /// resolved from the engine the <see cref="RagEngineFactory"/> selects for
    /// <c>Gert:Rag:Type</c>; <c>TryAdd</c> so a host or test may override it with a fake.
    /// </summary>
    public static IServiceCollection AddGertRag(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Bind + validate the engine selection at startup (fail at boot, not first use).
        services.AddOptions<RagOptions>()
            .Bind(configuration.GetSection(RagOptions.SectionName))
            .ValidateOnStart();

        // The keyed-plugin selector, closed over IConfiguration (host-agnostic registration).
        services.TryAddSingleton(sp => new RagEngineFactory(sp, configuration));

        // The port the service layer drives, built by the selected engine. The engine is
        // resolved + cached once by the factory; the provider is stateless over bound options +
        // open-per-use connections, so a singleton.
        services.TryAddSingleton<IRagIndexProvider>(sp =>
            sp.GetRequiredService<RagEngineFactory>().Engine().BuildRagIndexProvider());

        return services;
    }
}
