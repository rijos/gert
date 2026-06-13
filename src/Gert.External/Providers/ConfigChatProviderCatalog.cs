using Gert.External.OpenAI;
using Gert.Model;
using Gert.Service.External;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Gert.External.Providers;

/// <summary>
/// The operator chat-provider catalog (configuration.md section providers): the
/// <c>Gert:Providers</c> map, in configured (document) order. When the section is
/// absent or empty, a single default <c>openai</c> provider is synthesized from
/// <c>Gert:OpenAI:BaseUrl</c> so the picker - and a zero-config boot - always has
/// one real option. The same resolved list answers <see cref="SupportsTools"/>, so
/// the tool gate and the picker can never disagree; <see cref="Resolve"/> hands the
/// chat-client factory the full connection + sampling (the api-key secret included).
/// </summary>
public sealed class ConfigChatProviderCatalog : IChatProviderCatalog
{
    private readonly IReadOnlyList<(string Id, ChatProviderOptions Options)> _providers;
    private readonly IReadOnlyList<ChatProviderInfo> _infos;

    /// <summary>Bind the catalog once - configuration is fixed for the host's lifetime.</summary>
    public ConfigChatProviderCatalog(IConfiguration configuration, IOptions<OpenAIOptions> openai)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(openai);

        // GetChildren() preserves the JSON document order of the map keys, so the
        // picker order is the order the operator wrote - a plain Get<Dictionary<>>
        // would not promise that.
        var entries = configuration.GetSection(ChatProviderOptions.SectionName).GetChildren()
            .Select(c => (Id: c.Key, Options: c.Get<ChatProviderOptions>() ?? new ChatProviderOptions()))
            .ToList();

        if (entries.Count == 0)
        {
            entries = [(ChatProviderInfo.DefaultId, Fallback(openai.Value.BaseUrl))];
        }

        _providers = entries;
        _infos = entries.Select(e => e.Options.ToInfo(e.Id)).ToList();
    }

    // The single-vLLM deployment this fallback exists for: one permissive openai
    // provider (assume capable rather than silently cripple) pointed at the bound
    // base URL with the "default" upstream model. An operator who configures
    // Gert:Providers takes over completely, this entry included.
    private static ChatProviderOptions Fallback(string baseUrl) => new()
    {
        Name = "Default",
        Type = "openai",
        Default = true,
        Capabilities = [ChatProviderInfo.ToolsCapability, ChatProviderInfo.VisionCapability],
        Parameters = new ChatProviderParameters { BaseUrl = baseUrl, Model = "default" },
    };

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
    /// Resolve the full provider config (connection + sampling) for the chat-client
    /// factory. The <see cref="ChatProviderInfo.DefaultId"/> sentinel, an unset id, and an
    /// unknown id all resolve to the default provider - the same resolution the
    /// capability gate uses, so the gated and the called provider can never diverge.
    /// </summary>
    public (string Id, ChatProviderOptions Options) Resolve(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id) && id != ChatProviderInfo.DefaultId)
        {
            foreach (var entry in _providers)
            {
                if (entry.Id == id)
                {
                    return entry;
                }
            }
        }

        foreach (var entry in _providers)
        {
            if (entry.Options.Default)
            {
                return entry;
            }
        }

        // _providers always has at least the synthesized fallback, so this is safe.
        return _providers[0];
    }
}
