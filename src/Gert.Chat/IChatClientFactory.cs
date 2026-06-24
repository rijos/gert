using Microsoft.Extensions.AI;

namespace Gert.Chat;

/// <summary>
/// Resolves the <see cref="IChatClient"/> for a configured chat provider - the seam that lets
/// one turn talk to the right named provider with its own connection + sampling. The real
/// implementation resolves the provider catalog and dispatches to the keyed plugin builder;
/// <c>TurnRunner</c> asks for a client by the conversation's provider id (tech-stack.md section
/// Model API). The returned client is a Microsoft.Extensions.AI <see cref="IChatClient"/> - the
/// provider's own sampling + vendor extensions ride inside it (decisions #13).
/// </summary>
public interface IChatClientFactory
{
    /// <summary>
    /// The chat client for provider <paramref name="providerId"/> (the conversation's model id /
    /// <c>Gert:Chat:Providers</c> slug). The <c>default</c> sentinel and an unset/unknown id resolve
    /// to the catalog's default provider. Throws when no providers are configured.
    /// </summary>
    IChatClient ForProvider(string? providerId);
}
