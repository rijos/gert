using Gert.Model;
using Gert.Model.Chat;

namespace Gert.Chat;

/// <summary>
/// The configured chat provider catalog (rest-api.md section models) - a port like the
/// other outside-world seams: the real implementation reads operator config
/// (<c>Gert:Chat:Providers</c>) in <c>Gert.Chat</c>; hosts without one fall back to
/// <see cref="NullChatProviderCatalog"/> (empty, permissive). Returns only the
/// secret-free <see cref="ChatProviderInfo"/>; the connection + sampling (and the F8
/// api-key secret) stay host-side, reached by the chat-client factory. Extends
/// <see cref="IModelIdCatalog"/> so the validation layer can allow-list a request's model_id
/// against the configured slugs through a Gert.Model port (no inward edge to Gert.Chat).
/// </summary>
public interface IChatProviderCatalog : IModelIdCatalog
{
    /// <summary>The catalog entries, in configured order.</summary>
    IReadOnlyList<ChatProviderInfo> List();

    /// <summary>The provider with id <paramref name="id"/>, or null if unknown.</summary>
    ChatProviderInfo? Get(string id);

    /// <summary>
    /// The catalog's default provider - the one named by <c>Gert:Chat:DefaultProvider</c>, else
    /// the first entry - or null when the catalog is empty. Resolves the
    /// <see cref="ChatProviderInfo.DefaultId"/> sentinel a conversation may carry.
    /// </summary>
    ChatProviderInfo? Default();

    /// <summary>
    /// Whether <paramref name="id"/> may be offered tools. Unknown ids and entries
    /// without declared capabilities are PERMISSIVE (true) - only a provider that
    /// declares capabilities without <c>tools</c> gates.
    /// </summary>
    bool SupportsTools(string id);

    /// <summary>
    /// Whether <paramref name="id"/> accepts image input. Same permissive stance as
    /// <see cref="SupportsTools"/>: only a provider that declares capabilities
    /// without <c>vision</c> gates - the planner then drops images from the upstream
    /// prompt rather than erroring the turn.
    /// </summary>
    bool SupportsVision(string id);

    /// <summary>
    /// The context window (tokens) of <paramref name="id"/>, or null when unknown (an empty
    /// catalog / the synthesized zero-config default). Configured providers must declare it
    /// (fail-closed at startup); the planner uses it to bound an inline attachment's size.
    /// </summary>
    int? ContextSize(string id);
}

/// <summary>Empty, permissive catalog - the default when no host wires a real one.</summary>
public sealed class NullChatProviderCatalog : IChatProviderCatalog
{
    /// <inheritdoc />
    public IReadOnlySet<string> ModelIds { get; } = new HashSet<string>();

    /// <inheritdoc />
    public IReadOnlyList<ChatProviderInfo> List() => [];

    /// <inheritdoc />
    public ChatProviderInfo? Get(string id) => null;

    /// <inheritdoc />
    public ChatProviderInfo? Default() => null;

    /// <inheritdoc />
    public bool SupportsTools(string id) => true;

    /// <inheritdoc />
    public bool SupportsVision(string id) => true;

    /// <inheritdoc />
    public int? ContextSize(string id) => null;
}
