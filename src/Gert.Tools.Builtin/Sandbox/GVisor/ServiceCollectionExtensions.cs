using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Gert.Tools.Builtin.Sandbox.GVisor;

/// <summary>
/// DI registration for the <c>GVisor</c> code-sandbox implementation plugin (tech-stack.md
/// section Architecture; chat-and-tools.md section sandbox; security F5). Call after
/// <c>AddGertTools</c> to make the plugin available; configuration selects it via
/// <c>Gert:Tools:Sandbox:Type = GVisor</c>, after which the generic
/// <see cref="PythonSandboxFactory"/> dispatches to the keyed builder by Type with no central switch.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Register the gVisor sandbox plugin: bound parameters + the keyed builder.</summary>
    public static IServiceCollection AddGertSandboxGVisor(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<GVisorParameters>()
            .Bind(configuration.GetSection(GVisorParameters.SectionName))
            .ValidateOnStart();

        services.AddKeyedSingleton<IPythonSandboxBuilder, GVisorSandboxBuilder>(
            PythonSandboxFactory.NormalizeType("GVisor"));

        return services;
    }
}
