using Gert.Service.External;

namespace Gert.Testing.Fakes;

/// <summary>
/// An <see cref="IChatClientFactory"/> double that returns one fixed
/// <see cref="IChatModelClient"/> for every provider id - the test seam now that
/// <c>TurnRunner</c> resolves its client by provider rather than taking it directly.
/// </summary>
public sealed class FixedChatClientFactory(IChatModelClient client) : IChatClientFactory
{
    private readonly IChatModelClient _client = client ?? throw new ArgumentNullException(nameof(client));

    /// <inheritdoc />
    public IChatModelClient ForProvider(string? providerId) => _client;
}
