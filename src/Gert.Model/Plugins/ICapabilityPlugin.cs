namespace Gert.Model.Plugins;

/// <summary>
/// The common contract every capability IMPLEMENTATION plugin implements (tech-stack.md
/// section Architecture: functionality -&gt; Type -&gt; configuration). A capability (chat,
/// storage, ...) is a port; each impl is a self-registering plugin identified by its
/// configuration <see cref="Type"/> token. The host is the composition root: it registers
/// the plugins keyed by <see cref="Type"/>, and configuration selects which one builds a
/// given item - no central <c>switch</c> over Type anywhere.
///
/// <para>
/// A capability defines its own builder interface extending this one (e.g.
/// <c>IChatModelClientBuilder</c> adds <c>Build</c>) so the generic factory can resolve and
/// delegate. The contracts-vs-impl split is enforced by a meta architecture test: plugin
/// impls live only in the per-impl leaf (e.g. <c>Gert.Chat.OpenAI</c>), never in the
/// contracts assembly (<c>Gert.Chat</c>).
/// </para>
/// </summary>
public interface ICapabilityPlugin
{
    /// <summary>
    /// The configuration <c>Type</c> token this plugin handles, matched case-insensitively
    /// (e.g. <c>OpenAI</c>) - the factory normalizes a configured item's <c>Type</c> the same
    /// way, so casing in <c>appsettings.json</c> never matters.
    /// </summary>
    string Type { get; }
}
