namespace Gert.Model.Plugins;

/// <summary>
/// The common contract every capability IMPLEMENTATION plugin implements (tech-stack.md
/// section Architecture: functionality -&gt; Type -&gt; configuration). A "capability" (chat,
/// storage, ...) is a port the service layer drives; each concrete implementation is a
/// self-registering plugin identified by its configuration <see cref="Type"/> token. The host
/// is the composition root: it registers the plugins it wants available (keyed by
/// <see cref="Type"/>), and configuration selects at runtime which one builds a given item -
/// no central <c>switch</c> over Type anywhere.
///
/// <para>
/// A capability defines its own builder interface extending this one (e.g.
/// <c>IChatModelClientBuilder</c> adds a <c>Build</c> method), so the generic factory can
/// resolve the right plugin for a configured <see cref="Type"/> and delegate. The
/// contracts-vs-impl split is enforced: plugin implementations live in the per-impl leaf
/// assembly (e.g. <c>Gert.Chat.OpenAI</c>), never in the capability's contracts assembly
/// (<c>Gert.Chat</c>) - a meta architecture test asserts this for every capability.
/// </para>
/// </summary>
public interface ICapabilityPlugin
{
    /// <summary>
    /// The configuration <c>Type</c> token this plugin handles, matched case-insensitively
    /// (e.g. <c>OpenAI</c>). The factory normalizes a configured item's <c>Type</c> the same
    /// way to resolve the keyed plugin, so casing in <c>appsettings.json</c> never matters.
    /// </summary>
    string Type { get; }
}
