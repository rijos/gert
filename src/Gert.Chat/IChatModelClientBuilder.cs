using Gert.Model.Plugins;

namespace Gert.Chat;

/// <summary>
/// A chat-client plugin: builds the <see cref="IChatModelClient"/> for one provider
/// <see cref="ICapabilityPlugin.Type"/> (e.g. <c>OpenAI</c>). Each implementation assembly
/// registers exactly one, keyed by its Type; <see cref="ChatClientFactory"/> resolves the
/// builder by Type and delegates - there is no central <c>switch</c>. The builder owns its
/// connection + sampling config (binding its own <c>Parameters</c> shape), so the contracts
/// assembly stays implementation-agnostic.
/// </summary>
public interface IChatModelClientBuilder : ICapabilityPlugin
{
    /// <summary>
    /// Build the chat client for provider <paramref name="providerId"/> (the
    /// <c>Gert:Chat:Providers</c> slug, already resolved to this builder's Type by the factory).
    /// The plugin resolves that slug's connection + sampling + transport from its own
    /// registrations (named options / named <c>HttpClient</c>).
    /// </summary>
    IChatModelClient Build(string providerId);
}
