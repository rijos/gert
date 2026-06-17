using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Gert.Chat;

/// <summary>
/// One-call DI registration for the GENERIC chat layer (tech-stack.md section Architecture):
/// the implementation-agnostic provider catalog (<see cref="ConfigChatProviderCatalog"/>) over
/// <c>Gert:Chat:Providers</c> and the keyed-plugin <see cref="ChatClientFactory"/>. It registers
/// no implementation - the composition root adds the chat plugins it wants available (each
/// <c>AddGertChat&lt;Impl&gt;</c>, e.g. <c>AddGertChatOpenAI</c>), and configuration selects which
/// plugin builds a given provider at runtime. The service layer keeps talking only to the ports
/// (<see cref="IChatModelClient"/>/<see cref="IChatClientFactory"/>,
/// <see cref="IEmbeddingClient"/>, <see cref="IChatProviderCatalog"/>).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the generic chat provider catalog + the keyed-plugin chat-client factory. Bind
    /// the catalog over <paramref name="configuration"/> (closed over rather than resolved from
    /// the container, keeping the registration host-agnostic); the catalog uses an optional
    /// <see cref="IDefaultChatProvider"/> (contributed by an implementation plugin) for the
    /// zero-config synthesized default.
    /// </summary>
    public static IServiceCollection AddGertChat(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<ConfigChatProviderCatalog>(sp => new ConfigChatProviderCatalog(
            configuration,
            sp.GetService<IDefaultChatProvider>()));
        services.AddSingleton<IChatProviderCatalog>(sp => sp.GetRequiredService<ConfigChatProviderCatalog>());
        services.AddSingleton<IChatClientFactory>(sp => new ChatClientFactory(
            sp,
            sp.GetRequiredService<ConfigChatProviderCatalog>()));

        return services;
    }
}
