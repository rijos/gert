using FluentAssertions;
using Gert.Service.External;
using Gert.Tools.Sandbox.GVisor;
using Gert.Tools.Sandbox.Monty;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gert.Tools.Tests;

/// <summary>
/// The <c>Gert:Tools:Sandbox:Type</c> selector: both backends are self-registering keyed
/// plugins (<c>AddGertSandboxMonty</c> / <c>AddGertSandboxGVisor</c>) behind the one
/// <see cref="IPythonSandbox"/> port, the generic factory (in <c>AddGertTools</c>) dispatches by
/// Type with no central switch, and an unknown value fails (at first resolution) with an
/// actionable message. The default is <c>Monty</c> (no container infra needed).
/// </summary>
public sealed class SandboxBackendSelectionTests
{
    private static ServiceProvider Provider(string? type)
    {
        var settings = new Dictionary<string, string?>();
        if (type is not null)
        {
            settings["Gert:Tools:Sandbox:Type"] = type;
        }

        return Build(settings);
    }

    // The composition root registers all shipped plugins; configuration picks the active one.
    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGertTools(configuration);
        services.AddGertSandboxMonty(configuration);
        services.AddGertSandboxGVisor(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Default_backend_is_monty()
    {
        using var provider = Provider(null);
        provider.GetRequiredService<IPythonSandbox>().Should().BeOfType<MontySandbox>();
    }

    [Theory]
    [InlineData("monty")]
    [InlineData("Monty")]
    [InlineData(" Monty ")]
    public void Monty_backend_resolves_monty(string value)
    {
        using var provider = Provider(value);
        provider.GetRequiredService<IPythonSandbox>().Should().BeOfType<MontySandbox>();
    }

    [Theory]
    [InlineData("gvisor")]
    [InlineData("GViSoR")]
    public void Gvisor_backend_resolves_gvisor(string value)
    {
        using var provider = Provider(value);
        provider.GetRequiredService<IPythonSandbox>().Should().BeOfType<GVisorSandbox>();
    }

    [Fact]
    public void Unknown_backend_fails_with_an_actionable_message()
    {
        // No plugin is keyed under "docker", so the generic factory throws when the port is
        // first resolved (the keyed-plugin dispatch, mirroring the chat factory) - naming the
        // bad value and the registrars to add.
        using var provider = Build(new Dictionary<string, string?> { ["Gert:Tools:Sandbox:Type"] = "docker" });

        provider.Invoking(p => p.GetRequiredService<IPythonSandbox>())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*docker*")
            .WithMessage("*AddGertSandbox*");
    }
}
