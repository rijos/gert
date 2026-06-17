using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Gert.Tools.Sandbox.GVisor;

/// <summary>
/// DI registration for the <c>GVisor</c> code-sandbox IMPLEMENTATION plugin (tech-stack.md
/// section Architecture; chat-and-tools.md section sandbox; security F5). The composition root
/// calls <c>AddGertTools</c> (the generic sandbox selector + the cross-backend
/// <see cref="PythonSandboxOptions"/> caps) and then this method to make the gVisor plugin
/// available; configuration selects it via <c>Gert:Tools:Sandbox:Type = GVisor</c>. This
/// registers the bound <see cref="GVisorParameters"/> knobs and the keyed
/// <see cref="GVisorSandboxBuilder"/>; the generic <see cref="PythonSandboxFactory"/> dispatches
/// to it by Type with no central switch.
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

        // Self-register the keyed plugin; the generic PythonSandboxFactory dispatches by Type.
        services.AddKeyedSingleton<IPythonSandboxBuilder, GVisorSandboxBuilder>(
            PythonSandboxFactory.NormalizeType("GVisor"));

        return services;
    }
}
