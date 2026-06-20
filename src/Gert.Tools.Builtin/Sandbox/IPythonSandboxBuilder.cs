using Gert.Model.Plugins;
using Gert.Tools;
using Gert.Tools.Ports;

namespace Gert.Tools.Sandbox;

/// <summary>
/// A code-sandbox plugin: builds the <see cref="IPythonSandbox"/> for one backend
/// <see cref="ICapabilityPlugin.Type"/> (e.g. <c>Monty</c> / <c>GVisor</c>). Each implementation
/// registers exactly one, keyed by its Type; the generic <see cref="PythonSandboxFactory"/>
/// resolves the builder for the configured <c>Gert:Tools:Sandbox:Type</c> and delegates - there
/// is no central <c>switch</c> over Type (mirrors <c>Gert.Chat.IChatModelClientBuilder</c>). The
/// builder owns its backend's connection/knobs, so the generic layer stays backend-agnostic.
/// </summary>
public interface IPythonSandboxBuilder : ICapabilityPlugin
{
    /// <summary>
    /// Build the sandbox (this builder's Type has already been selected by the factory). The
    /// plugin resolves its backend-specific options + transport from its own registrations.
    /// </summary>
    IPythonSandbox Build();
}
