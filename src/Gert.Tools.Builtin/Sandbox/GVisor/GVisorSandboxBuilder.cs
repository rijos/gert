using Gert.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gert.Tools.Sandbox.GVisor;

/// <summary>
/// The <c>GVisor</c> code-sandbox plugin (<see cref="IPythonSandboxBuilder"/>): builds a
/// <see cref="GVisorSandbox"/> over the cross-backend <see cref="PythonSandboxOptions"/> caps +
/// the gVisor-specific <see cref="GVisorParameters"/>. Registered keyed by its <see cref="Type"/>
/// in <c>AddGertSandboxGVisor</c>; the generic <see cref="PythonSandboxFactory"/> resolves it
/// when <c>Gert:Tools:Sandbox:Type</c> is <c>GVisor</c> - no central switch over Type.
/// </summary>
public sealed class GVisorSandboxBuilder : IPythonSandboxBuilder
{
    private readonly IOptions<PythonSandboxOptions> _options;
    private readonly IOptions<GVisorParameters> _parameters;
    private readonly ILoggerFactory _loggerFactory;

    public GVisorSandboxBuilder(
        IOptions<PythonSandboxOptions> options,
        IOptions<GVisorParameters> parameters,
        ILoggerFactory loggerFactory)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <inheritdoc />
    public string Type => "GVisor";

    /// <inheritdoc />
    public IPythonSandbox Build() => new GVisorSandbox(
        _options,
        _parameters,
        _loggerFactory.CreateLogger<GVisorSandbox>());
}
