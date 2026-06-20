using Gert.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gert.Tools.Sandbox.Monty;

/// <summary>
/// The <c>Monty</c> code-sandbox plugin (<see cref="IPythonSandboxBuilder"/>): builds a
/// <see cref="MontySandbox"/> over the named monty <c>HttpClient</c> + the cross-backend
/// <see cref="PythonSandboxOptions"/> caps. Registered keyed by its <see cref="Type"/> in
/// <c>AddGertSandboxMonty</c>; the generic <see cref="PythonSandboxFactory"/> resolves it when
/// <c>Gert:Tools:Sandbox:Type</c> is <c>Monty</c> - no central switch over Type.
/// </summary>
public sealed class MontySandboxBuilder : IPythonSandboxBuilder
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptions<PythonSandboxOptions> _options;
    private readonly ILoggerFactory _loggerFactory;

    public MontySandboxBuilder(
        IHttpClientFactory httpFactory,
        IOptions<PythonSandboxOptions> options,
        ILoggerFactory loggerFactory)
    {
        _httpFactory = httpFactory ?? throw new ArgumentNullException(nameof(httpFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <inheritdoc />
    public string Type => "Monty";

    /// <inheritdoc />
    public IPythonSandbox Build() => new MontySandbox(
        _httpFactory.CreateClient(MontySandbox.HttpClientName),
        _options,
        _loggerFactory.CreateLogger<MontySandbox>());
}
