using Gert.Chat;
using Microsoft.Extensions.AI;

namespace Gert.Testing.Fakes;

/// <summary>
/// An <see cref="IChatClientFactory"/> double that returns one fixed
/// <see cref="IChatClient"/> for every provider id - the test seam now that
/// <c>TurnRunner</c> resolves its client by provider rather than taking it directly.
/// </summary>
public sealed class FixedChatClientFactory(IChatClient client) : IChatClientFactory
{
    private readonly IChatClient _client = client ?? throw new ArgumentNullException(nameof(client));

    /// <inheritdoc />
    public IChatClient ForProvider(string? providerId) => _client;
}
