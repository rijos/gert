using Gert.Service.External;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Gert.Tools.Sandbox;

/// <summary>
/// Resolves the single <see cref="IPythonSandbox"/> backend the operator selected via
/// <c>Gert:Tools:Sandbox:Type</c> (default <c>Monty</c> - it needs no container infra): it
/// looks up the registered <see cref="IPythonSandboxBuilder"/> plugin keyed by that Type and
/// builds it - keyed DI, no central <c>switch</c> over Type (mirrors
/// <c>Gert.Chat.ChatClientFactory</c>). The composition root registers the shipped plugins
/// (<c>AddGertSandboxMonty</c> / <c>AddGertSandboxGVisor</c>); a selected Type with no
/// registered plugin fails at first resolution with an actionable message.
/// </summary>
public sealed class PythonSandboxFactory
{
    /// <summary>The configuration key selecting the sandbox backend.</summary>
    public const string TypeKey = "Gert:Tools:Sandbox:Type";

    /// <summary>The backend assumed when <see cref="TypeKey"/> is unset (no container infra needed).</summary>
    public const string DefaultType = "Monty";

    private readonly IServiceProvider _services;
    private readonly string _type;

    /// <summary>
    /// Capture the configured Type once - configuration is fixed for the host's lifetime
    /// (constructed over the closed-over <see cref="IConfiguration"/> in the registrar, keeping
    /// the registration host-agnostic rather than resolving <see cref="IConfiguration"/> from DI).
    /// </summary>
    public PythonSandboxFactory(IServiceProvider services, IConfiguration configuration)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        ArgumentNullException.ThrowIfNull(configuration);

        var configured = configuration[TypeKey];
        _type = string.IsNullOrWhiteSpace(configured) ? DefaultType : configured;
    }

    /// <summary>
    /// Normalize a configuration <c>Type</c> token to the keyed-plugin lookup key (each plugin
    /// registers under the same normalization), so casing in <c>appsettings.json</c> never matters.
    /// </summary>
    public static string NormalizeType(string? type) => (type ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>
    /// Build the selected sandbox by dispatching to its keyed plugin. Throws when the configured
    /// Type has no registered plugin (the unknown-backend failure mode).
    /// </summary>
    public IPythonSandbox Create() =>
        (_services.GetKeyedService<IPythonSandboxBuilder>(NormalizeType(_type))
            ?? throw new InvalidOperationException(
                $"{TypeKey} '{_type}' has no registered sandbox plugin. Register its implementation " +
                "(e.g. AddGertSandboxMonty / AddGertSandboxGVisor) in the composition root. " +
                "Use 'Monty' or 'GVisor'."))
        .Build();
}
