namespace Gert.Chat;

/// <summary>
/// Resolves the <see cref="IChatModelClient"/> for a configured chat provider -
/// the seam that lets one turn talk to the right named provider with its own
/// connection + sampling. The real implementation lives in <c>Gert.Chat</c>
/// over the provider catalog; <c>TurnRunner</c> asks for a client by the
/// conversation's provider id.
/// </summary>
public interface IChatClientFactory
{
    /// <summary>
    /// The chat client for provider <paramref name="providerId"/> (the
    /// conversation's model id / <c>Gert:Chat:Providers</c> slug). The <c>default</c>
    /// sentinel and an unset/unknown id resolve to the catalog's default provider.
    /// Throws when no providers are configured.
    /// </summary>
    IChatModelClient ForProvider(string? providerId);
}
