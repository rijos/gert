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
/// The default entry is named by <c>Gert:Chat:DefaultProvider</c> (a slug); unset falls back to
/// the first configured entry, and a name matching no provider is a fail-closed config error.
/// The same resolved list answers <see cref="SupportsTools"/>, so the tool gate and the picker
/// can never disagree; <see cref="Resolve"/> hands the chat-client factory the provider's id +
/// Type to dispatch to the right plugin.
/// </summary>
public sealed class ConfigChatProviderCatalog : IChatProviderCatalog
{
    private readonly IReadOnlyList<ChatProviderInfo> _infos;
    private readonly IReadOnlySet<string> _modelIds;

    public ConfigChatProviderCatalog(IConfiguration configuration, IDefaultChatProvider? defaultProvider = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // GetChildren() preserves the JSON document order of the map keys, so the
        // picker order is the order the operator wrote - a plain Get<Dictionary<>>
        // would not promise that. The endpoint badge is read generically from each
        // entry's Parameters:BaseUrl (a near-universal "where it connects" convention),
        // so the catalog never binds an implementation's Parameters shape. A configured entry
        // that omits Type takes the registered default plugin's token (the OpenAI plugin's
        // `openai`), so this generic catalog never names an implementation itself.
        var defaultType = defaultProvider?.DefaultType;
        var entries = configuration.GetSection(ChatProviderOptions.SectionName).GetChildren()
            .Select(c =>
            {
                var options = c.Get<ChatProviderOptions>() ?? new ChatProviderOptions();
                if (string.IsNullOrWhiteSpace(options.Type) && !string.IsNullOrWhiteSpace(defaultType))
                {
                    options.Type = defaultType;
                }

                return options.ToInfo(c.Key, c.GetSection("Parameters")["BaseUrl"]);
            })
            .ToList();

        // Every CONFIGURED provider must declare its context window (tokens) - the planner needs it
        // to bound an inline attachment against the model's context. Fail closed and name the
        // offenders + the key, like the default-selection check below. (The synthesized zero-config
        // default is exempt: it has no config entry to carry a Context, and the inline gate simply
        // skips when the size is unknown.)
        var missingContext = entries.Where(e => e.Context is null or <= 0).Select(e => e.Id).ToList();
        if (missingContext.Count > 0)
        {
            throw new InvalidOperationException(
                $"Chat provider(s) {string.Join(", ", missingContext)} are missing a positive "
                + $"'Context' (context window in tokens). Set {ChatProviderOptions.SectionName}:<slug>:Context "
                + "for each.");
        }

        if (entries.Count == 0 && defaultProvider?.Synthesize() is { } synthesized)
        {
            entries = [synthesized];
        }

        _infos = ApplyDefaultSelection(entries, configuration[ChatProviderOptions.DefaultProviderKey]);

        // The allow-list the validation layer (via IModelIdCatalog) checks model_id against.
        // Ordinal to match Get's case-sensitive lookup, so the validator and the resolver agree.
        _modelIds = _infos.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public IReadOnlySet<string> ModelIds => _modelIds;

    // Mark exactly one entry as the cascade default, selected by Gert:Chat:DefaultProvider (a
    // provider slug). Unset -> the first entry (document order) wins via Default(). A name that
    // matches no provider is an operator typo, not a silent fallback: fail closed and name the
    // valid slugs, so a misconfigured default can never quietly resolve to the wrong provider.
    private static IReadOnlyList<ChatProviderInfo> ApplyDefaultSelection(
        List<ChatProviderInfo> entries, string? defaultName)
    {
        if (string.IsNullOrWhiteSpace(defaultName))
        {
            return entries;
        }

        if (!entries.Any(e => string.Equals(e.Id, defaultName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"{ChatProviderOptions.DefaultProviderKey} '{defaultName}' matches no configured chat " +
                $"provider. Set it to one of: {string.Join(", ", entries.Select(e => e.Id))}.");
        }

        return entries
            .Select(e => e with { Default = string.Equals(e.Id, defaultName, StringComparison.OrdinalIgnoreCase) })
            .ToList();
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

    /// <inheritdoc />
    public int? ContextSize(string id) => (Get(id) ?? Default())?.Context;

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
