namespace Gert.Model.Chat;

/// <summary>
/// One entry in the chat provider catalog (rest-api.md section models) - a named,
/// operator-configured preset behind <c>GET /api/models</c> and the capability
/// gate for tool calling / vision. The connection + sampling live host-side in
/// <c>Gert:Chat:Providers</c> (configuration.md); this is the secret-free view the
/// picker and the tool gate see. Only <see cref="Id"/> + <see cref="Name"/> are
/// required.
/// </summary>
public sealed record ChatProviderInfo
{
    /// <summary>
    /// The sentinel id meaning "the operator-configured default provider" - the
    /// fallback when neither the request nor the conversation supplies one,
    /// resolved to the catalog's <see cref="Default"/> entry.
    /// </summary>
    public const string DefaultId = "default";

    /// <summary>The capability token that marks a provider as tool-calling capable.</summary>
    public const string ToolsCapability = "tools";

    /// <summary>The capability token that marks a provider as vision (image input) capable.</summary>
    public const string VisionCapability = "vision";

    /// <summary>
    /// Provider key - the <c>Gert:Chat:Providers</c> map slug. Stored as the
    /// conversation's model id and sent back by the picker; resolves to the
    /// provider's connection + sampling host-side. (Wire name: <c>id</c>.)
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Display name for the picker (arbitrary, e.g. "Qwen36 - thinking").</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Provider type - selects the chat-client implementation behind the abstraction
    /// (e.g. <c>openai</c> for an OpenAI-compatible/vLLM endpoint). Display-only here. No baked-in
    /// default: a POCO knows no implementation; the catalog fills an omitted type from the
    /// registered default plugin.
    /// </summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>The catalog's flagged default (the picker's initial selection).</summary>
    public bool Default { get; init; }

    /// <summary>
    /// Capability tokens (<c>tools</c>, <c>vision</c>, ...). <c>null</c> = undeclared:
    /// the provider is assumed capable rather than silently crippled.
    /// </summary>
    public IReadOnlyList<string>? Capabilities { get; init; }

    /// <summary>Context window in tokens (badge: "128K ctx"). Null = unknown.</summary>
    public int? Context { get; init; }

    /// <summary>Whether the picker shows a "fast" hint for this provider.</summary>
    public bool Fast { get; init; }

    /// <summary>Display-only endpoint hint, e.g. <c>:8001</c>.</summary>
    public string? Endpoint { get; init; }

    /// <summary>Tool-calling capable - declared, or undeclared (permissive).</summary>
    public bool SupportsTools => Capabilities is null || Capabilities.Contains(ToolsCapability);

    /// <summary>Vision (image input) capable - declared, or undeclared (permissive).</summary>
    public bool SupportsVision => Capabilities is null || Capabilities.Contains(VisionCapability);
}
