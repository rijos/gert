using Gert.Model.Plugins;
using Microsoft.Extensions.AI;

namespace Gert.Chat;

/// <summary>
/// A chat-client plugin: builds the <see cref="IChatClient"/> for one provider
/// <see cref="ICapabilityPlugin.Type"/> (e.g. <c>OpenAI</c>). Each implementation assembly
/// registers exactly one, keyed by its Type; <see cref="ChatClientFactory"/> resolves the
/// builder by Type and delegates - there is no central <c>switch</c>. The builder owns its
/// connection + sampling config (binding its own <c>Parameters</c> shape) and any vendor-specific
/// wrapping (e.g. the OpenAI plugin's stream-salvage <c>DelegatingChatClient</c>), so the
/// contracts assembly stays implementation-agnostic.
/// </summary>
public interface IChatModelClientBuilder : ICapabilityPlugin
{
    /// <summary>
    /// Build the chat client for provider <paramref name="providerId"/> (the
    /// <c>Gert:Chat:Providers</c> slug, already resolved to this builder's Type by the factory).
    /// The plugin resolves that slug's connection + sampling + transport from its own
    /// registrations (named options / named <c>HttpClient</c>).
    /// </summary>
    IChatClient Build(string providerId);
}
