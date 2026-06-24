using Gert.Model.Chat;

namespace Gert.Chat;

/// <summary>
/// One configured chat provider's IMPLEMENTATION-AGNOSTIC metadata (configuration.md section
/// providers) - a named preset under Gert's chat abstraction. Bound from one
/// <c>Gert:Chat:Providers:&lt;slug&gt;</c> entry; the map key is the provider's id (see
/// <see cref="ToInfo"/>). <see cref="Type"/> selects which registered plugin builds the client;
/// the type-specific connection + sampling live under that entry's <c>Parameters</c> and are
/// bound by the chosen plugin, not here - so this generic catalog never sees an
/// implementation's option shape (or its secrets).
/// </summary>
public sealed class ChatProviderOptions
{
    /// <summary>The configuration section the provider map binds from.</summary>
    public const string SectionName = "Gert:Chat:Providers";

    /// <summary>The chat-level key naming the default provider's slug (a sibling of the map).</summary>
    public const string DefaultProviderKey = "Gert:Chat:DefaultProvider";

    /// <summary>Display name for the picker (arbitrary, e.g. "Qwen36 - thinking").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Provider type - selects the chat-client implementation plugin (matched
    /// case-insensitively; <c>openai</c> is the only plugin shipped today). Surfaced verbatim as the
    /// wire <c>type</c>. Empty = unset: this generic contract names no implementation, so the catalog
    /// fills an omitted type from the registered <see cref="IDefaultChatProvider"/> (the OpenAI plugin
    /// supplies <c>openai</c>) rather than baking the token in here.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Whether the picker shows a "fast" hint.</summary>
    public bool Fast { get; set; }

    /// <summary>
    /// Capability tokens (<c>tools</c>, <c>vision</c>, ...). <c>null</c> = undeclared
    /// (permissive: assumed capable).
    /// </summary>
    public IReadOnlyList<string>? Capabilities { get; set; }

    /// <summary>Context window in tokens (picker badge). Null = unknown.</summary>
    public int? Context { get; set; }

    /// <summary>
    /// Project to the secret-free <see cref="ChatProviderInfo"/> the API/picker see.
    /// <paramref name="id"/> is the map slug; <paramref name="endpoint"/> is the display-only
    /// connection hint the catalog reads generically from this entry's
    /// <c>Parameters:BaseUrl</c> (every chat implementation connects to some endpoint).
    /// </summary>
    public ChatProviderInfo ToInfo(string id, string? endpoint) => new()
    {
        Id = id,
        Name = string.IsNullOrWhiteSpace(Name) ? id : Name,
        Type = Type,
        Fast = Fast,
        Capabilities = Capabilities,
        Context = Context,
        Endpoint = endpoint,
    };
}
