using Gert.Model.Chat;
using Microsoft.Extensions.Configuration;

namespace Gert.Chat;

/// <summary>
/// The operator chat-provider catalog (configuration.md section providers): the
/// <c>Gert:Chat:Providers</c> map, in configured (document) order. Implementation-agnostic -
/// it binds only each provider's metadata (<see cref="ChatProviderOptions"/>) and reads the
/// display-only endpoint from <c>Parameters:BaseUrl</c>; the type-specific connection +
/// sampling stay with the chosen plugin. When the section is absent or empty, a single default
/// provider is synthesized from the registered <see cref="IDefaultChatProvider"/> (the OpenAI
/// plugin points it at <c>Gert:Embeddings:Parameters:BaseUrl</c>) so the picker - and a
/// zero-config boot - always has one real option; with no such plugin the catalog is empty.
/// The same resolved list answers <see cref="SupportsTools"/>, so the tool gate and the picker
/// can never disagree; <see cref="Resolve"/> hands the chat-client factory the provider's id +
/// Type to dispatch to the right plugin.
/// </summary>
public sealed class ConfigChatProviderCatalog : IChatProviderCatalog
{
    private readonly IReadOnlyList<ChatProviderInfo> _infos;

    /// <summary>Bind the catalog once - configuration is fixed for the host's lifetime.</summary>
    public ConfigChatProviderCatalog(IConfiguration configuration, IDefaultChatProvider? defaultProvider = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // GetChildren() preserves the JSON document order of the map keys, so the
        // picker order is the order the operator wrote - a plain Get<Dictionary<>>
        // would not promise that. The endpoint badge is read generically from each
        // entry's Parameters:BaseUrl (a near-universal "where it connects" convention),
        // so the catalog never binds an implementation's Parameters shape.
        var entries = configuration.GetSection(ChatProviderOptions.SectionName).GetChildren()
            .Select(c => (c.Get<ChatProviderOptions>() ?? new ChatProviderOptions())
                .ToInfo(c.Key, c.GetSection("Parameters")["BaseUrl"]))
            .ToList();

        if (entries.Count == 0 && defaultProvider?.Synthesize() is { } synthesized)
        {
            entries = [synthesized];
        }

        _infos = entries;
    }

    /// <inheritdoc />
    public IReadOnlyList<ChatProviderInfo> List() => _infos;

    /// <inheritdoc />
    public ChatProviderInfo? Get(string id) => _infos.FirstOrDefault(p => p.Id == id);

    /// <inheritdoc />
    public ChatProviderInfo? Default() => _infos.FirstOrDefault(p => p.Default) ?? _infos.FirstOrDefault();

    /// <inheritdoc />
    public bool SupportsTools(string id) => (Get(id) ?? Default())?.SupportsTools ?? true;

    /// <inheritdoc />
    public bool SupportsVision(string id) => (Get(id) ?? Default())?.SupportsVision ?? true;

    /// <summary>
    /// Resolve the provider (its id + Type) for the chat-client factory. The
    /// <see cref="ChatProviderInfo.DefaultId"/> sentinel, an unset id, and an unknown id all
    /// resolve to the default provider - the same resolution the capability gate uses, so the
    /// gated and the called provider can never diverge. Throws when no providers are configured
    /// (and no default plugin synthesized one), the empty-catalog failure mode.
    /// </summary>
    public ChatProviderInfo Resolve(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id) && id != ChatProviderInfo.DefaultId)
        {
            var hit = Get(id);
            if (hit is not null)
            {
                return hit;
            }
        }

        return Default()
            ?? throw new InvalidOperationException(
                "No chat providers are configured (Gert:Chat:Providers is empty and no default " +
                "provider plugin is registered).");
    }
}
