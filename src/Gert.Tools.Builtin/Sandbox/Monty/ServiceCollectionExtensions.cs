using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Gert.Tools.Sandbox.Monty;

/// <summary>
/// DI registration for the <c>Monty</c> code-sandbox IMPLEMENTATION plugin (tech-stack.md
/// section Architecture; chat-and-tools.md section sandbox). The composition root calls
/// <c>AddGertTools</c> (the generic sandbox selector + the cross-backend
/// <see cref="PythonSandboxOptions"/> caps) and then this method to make the monty plugin
/// available; configuration selects it via <c>Gert:Tools:Sandbox:Type = Monty</c> (the default).
/// This registers the bound <see cref="MontyParameters"/> connection, the named monty
/// <c>HttpClient</c>, the startup wall-clock relation check, and the keyed
/// <see cref="MontySandboxBuilder"/>; the generic <see cref="PythonSandboxFactory"/> dispatches
/// to it by Type with no central switch.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Register the monty sandbox plugin: bound parameters + named HttpClient + the keyed builder.</summary>
    public static IServiceCollection AddGertSandboxMonty(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<MontyParameters>()
            .Bind(configuration.GetSection(MontyParameters.SectionName))
            .ValidateOnStart();

        // Startup relation check (the HTTP backstop must sit above monty's wall clock, or the
        // transport would kill runs the interpreter was about to return cleanly). Both backend
        // plugins are registered at the composition root, so this validator only matters when
        // monty is the SELECTED backend - gated on the configured Type, captured here so a long
        // gVisor wall clock never demands a pointless monty knob change (and so the validator
        // needs no IConfiguration from DI). MontyParameters has ValidateOnStart, which picks it up.
        var montySelected = IsMontySelected(configuration);
        services.AddSingleton<IValidateOptions<MontyParameters>>(sp =>
            new MontySandboxTimeoutRelationValidator(
                sp.GetRequiredService<IOptions<PythonSandboxOptions>>(),
                montySelected));

        // A plain typed client: NO standard resilience handler. A sandbox run is not safely
        // retryable (re-running code wastes a run, and under code-mode would re-invoke
        // tools), so the only time bounds are monty's own wall clock + this HTTP backstop.
        services.AddHttpClient(MontySandbox.HttpClientName)
            .ConfigureHttpClient((sp, client) =>
            {
                var opt = sp.GetRequiredService<IOptions<MontyParameters>>().Value;
                client.BaseAddress = new Uri(opt.BaseUrl, UriKind.Absolute);
                client.Timeout = TimeSpan.FromSeconds(opt.RequestTimeoutSeconds);
            });

        // Self-register the keyed plugin; the generic PythonSandboxFactory dispatches by Type.
        services.AddKeyedSingleton<IPythonSandboxBuilder, MontySandboxBuilder>(
            PythonSandboxFactory.NormalizeType("Monty"));

        return services;
    }

    /// <summary>
    /// Whether monty is the selected sandbox backend: the configured <c>Gert:Tools:Sandbox:Type</c>
    /// is <c>Monty</c> or unset (monty is the default). The relation check applies only then.
    /// </summary>
    private static bool IsMontySelected(IConfiguration configuration)
    {
        var type = PythonSandboxFactory.NormalizeType(configuration[PythonSandboxFactory.TypeKey]);
        return type.Length == 0 || type == PythonSandboxFactory.NormalizeType("Monty");
    }

    /// <summary>
    /// Enforces at startup what <see cref="MontyParameters.RequestTimeoutSeconds"/> documents:
    /// the HTTP backstop sits strictly <b>above</b> <see cref="PythonSandboxOptions.WallClockSeconds"/>,
    /// so monty's own limit trips first and returns a clean timed-out result
    /// (chat-and-tools.md section sandbox). A no-op when monty is not the selected backend.
    /// </summary>
    private sealed class MontySandboxTimeoutRelationValidator : IValidateOptions<MontyParameters>
    {
        private readonly IOptions<PythonSandboxOptions> _sandbox;
        private readonly bool _montySelected;

        public MontySandboxTimeoutRelationValidator(IOptions<PythonSandboxOptions> sandbox, bool montySelected)
        {
            _sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));
            _montySelected = montySelected;
        }

        public ValidateOptionsResult Validate(string? name, MontyParameters options)
        {
            ArgumentNullException.ThrowIfNull(options);

            // gVisor does not use the monty sidecar, so these monty defaults are unused then -
            // a long gVisor wall clock must not demand a pointless monty knob change.
            if (!_montySelected)
            {
                return ValidateOptionsResult.Success;
            }

            var wallClock = _sandbox.Value.WallClockSeconds;
            if (options.RequestTimeoutSeconds <= wallClock)
            {
                return ValidateOptionsResult.Fail(
                    $"{MontyParameters.SectionName}:RequestTimeoutSeconds ({options.RequestTimeoutSeconds}s) " +
                    $"must be greater than {PythonSandboxOptions.SectionName}:WallClockSeconds ({wallClock}s): " +
                    "the HTTP timeout is only a backstop for a hung sidecar - monty's own wall clock must " +
                    "trip first so a long run returns a clean timed-out result instead of a transport error. " +
                    "Raise RequestTimeoutSeconds above WallClockSeconds (or lower WallClockSeconds).");
            }

            return ValidateOptionsResult.Success;
        }
    }
}
