using Gert.Model.Plugins;
using Gert.Tools;

namespace Gert.Tools.Search;

/// <summary>
/// A web-search plugin: builds the <see cref="IWebSearch"/> for one search
/// <see cref="ICapabilityPlugin.Type"/> (e.g. <c>SearXNG</c>). Each implementation registers
/// exactly one, keyed by its Type; the generic <see cref="WebSearchFactory"/> resolves the
/// builder for the configured <c>Gert:Tools:Search:Type</c> and delegates - there is no central
/// <c>switch</c> over Type (mirrors <c>Gert.Chat.IChatModelClientBuilder</c>). The builder owns
/// its implementation's connection config (its <c>Parameters</c> shape + transport), so the
/// generic layer stays implementation-agnostic.
/// </summary>
public interface IWebSearchBuilder : ICapabilityPlugin
{
    /// <summary>
    /// Build the search client (this builder's Type has already been selected by the factory).
    /// The plugin resolves its connection + transport from its own registrations.
    /// </summary>
    IWebSearch Build();
}
