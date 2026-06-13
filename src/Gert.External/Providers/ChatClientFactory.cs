using System.Collections.Concurrent;
using Gert.External.OpenAI;
using Gert.Service.External;
using Microsoft.Extensions.Logging;

namespace Gert.External.Providers;

/// <summary>
/// The real <see cref="IChatClientFactory"/>: resolves a provider id through the
/// <see cref="ConfigChatProviderCatalog"/> and hands back the matching
/// <see cref="IChatModelClient"/>, built from that provider's connection + sampling.
/// Clients are cached per resolved provider id (the SDK client is reusable and
/// thread-safe), so a hot conversation does not rebuild one per turn. <c>Type</c>
/// selects the implementation - only <c>openai</c> ships today.
/// </summary>
public sealed class ChatClientFactory : IChatClientFactory
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ConfigChatProviderCatalog _catalog;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<string, IChatModelClient> _cache = new(StringComparer.Ordinal);

    public ChatClientFactory(
        IHttpClientFactory httpFactory,
        ConfigChatProviderCatalog catalog,
        ILoggerFactory loggerFactory)
    {
        _httpFactory = httpFactory ?? throw new ArgumentNullException(nameof(httpFactory));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <inheritdoc />
    public IChatModelClient ForProvider(string? providerId)
    {
        var (id, options) = _catalog.Resolve(providerId);
        return _cache.GetOrAdd(id, _ => Create(id, options));
    }

    private IChatModelClient Create(string id, ChatProviderOptions options)
    {
        var type = (options.Type ?? "openai").Trim().ToLowerInvariant();
        return type switch
        {
            "" or "openai" => new OpenAIChatModelClient(
                _httpFactory.CreateClient(OpenAIChatModelClient.HttpClientName),
                options.Parameters,
                _loggerFactory.CreateLogger<OpenAIChatModelClient>()),
            _ => throw new InvalidOperationException(
                $"Chat provider '{id}' has unsupported Type '{options.Type}'. Supported: openai."),
        };
    }
}
