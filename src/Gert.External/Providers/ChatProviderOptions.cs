using Gert.Model;

namespace Gert.External.Providers;

/// <summary>
/// One configured chat provider (configuration.md section providers) - a named preset
/// under Gert's chat abstraction. Bound from one <c>Gert:Providers:&lt;slug&gt;</c>
/// entry; the map key is the provider's id (see <see cref="ToInfo"/>). The same
/// physical model can appear under several slugs with different
/// <see cref="Parameters"/> (e.g. a thinking preset vs an instruct preset).
/// </summary>
public sealed class ChatProviderOptions
{
    /// <summary>The configuration section the provider map binds from.</summary>
    public const string SectionName = "Gert:Providers";

    /// <summary>Display name for the picker (arbitrary, e.g. "Qwen36 - thinking").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Provider type - selects the chat-client implementation (<c>openai</c> today).</summary>
    public string Type { get; set; } = "openai";

    /// <summary>The picker's initial selection when nothing else is chosen.</summary>
    public bool Default { get; set; }

    /// <summary>Whether the picker shows a "fast" hint.</summary>
    public bool Fast { get; set; }

    /// <summary>
    /// Capability tokens (<c>tools</c>, <c>vision</c>, ...). <c>null</c> = undeclared
    /// (permissive: assumed capable). Mirrors the old model-catalog gate.
    /// </summary>
    public IReadOnlyList<string>? Capabilities { get; set; }

    /// <summary>Context window in tokens (picker badge). Null = unknown.</summary>
    public int? Context { get; set; }

    /// <summary>The type-specific connection + sampling bag.</summary>
    public ChatProviderParameters Parameters { get; set; } = new();

    /// <summary>
    /// Project to the secret-free <see cref="ChatProviderInfo"/> the API/picker see -
    /// the connection + sampling (and the <see cref="ChatProviderParameters.ApiKey"/>
    /// secret) stay host-side. <paramref name="id"/> is the map slug.
    /// </summary>
    public ChatProviderInfo ToInfo(string id) => new()
    {
        Id = id,
        Name = string.IsNullOrWhiteSpace(Name) ? id : Name,
        Type = Type,
        Default = Default,
        Fast = Fast,
        Capabilities = Capabilities,
        Context = Context,
        Endpoint = Parameters.BaseUrl,
    };
}
