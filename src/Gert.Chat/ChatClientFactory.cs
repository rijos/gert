using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Gert.Chat;

/// <summary>
/// The real <see cref="IChatClientFactory"/>: resolves a provider id through the
/// <see cref="ConfigChatProviderCatalog"/>, then dispatches to the registered
/// <see cref="IChatModelClientBuilder"/> plugin for that provider's <c>Type</c> (keyed DI -
/// no central <c>switch</c> over Type). Clients are cached per resolved provider id (the built
/// client is reusable and thread-safe), so a hot conversation does not rebuild one per turn.
/// </summary>
public sealed class ChatClientFactory : IChatClientFactory
{
    private readonly IServiceProvider _services;
    private readonly ConfigChatProviderCatalog _catalog;
    private readonly ConcurrentDictionary<string, IChatModelClient> _cache = new(StringComparer.Ordinal);

    public ChatClientFactory(IServiceProvider services, ConfigChatProviderCatalog catalog)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <summary>
    /// Normalize a configuration <c>Type</c> token to the keyed-plugin lookup key (the plugin
    /// registers under the same normalization), so casing in <c>appsettings.json</c> -
    /// <c>OpenAI</c> vs <c>openai</c> - never matters.
    /// </summary>
    public static string NormalizeType(string? type) => (type ?? string.Empty).Trim().ToLowerInvariant();

    /// <inheritdoc />
    public IChatModelClient ForProvider(string? providerId)
    {
        var info = _catalog.Resolve(providerId);
        return _cache.GetOrAdd(info.Id, _ => BuilderFor(info.Type).Build(info.Id));
    }

    private IChatModelClientBuilder BuilderFor(string type) =>
        _services.GetKeyedService<IChatModelClientBuilder>(NormalizeType(type))
        ?? throw new InvalidOperationException(
            $"Chat provider Type '{type}' has no registered plugin. Register its " +
            "implementation (e.g. AddGertChatOpenAI) in the composition root.");
}
