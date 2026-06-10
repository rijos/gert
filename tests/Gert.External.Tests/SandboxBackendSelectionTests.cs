using FluentAssertions;
using Gert.External.Sandbox;
using Gert.Service.External;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gert.External.Tests;

/// <summary>
/// The <c>Gert:Sandbox:Backend</c> selector in <c>AddGertExternal</c>: both backends sit
/// behind the one <see cref="ISandbox"/> port, the operator picks, and an unknown value
/// fails fast at registration. The default is <c>monty</c> (no container infra needed).
/// </summary>
public sealed class SandboxBackendSelectionTests
{
    private static ServiceProvider Provider(string? backend)
    {
        var settings = new Dictionary<string, string?>();
        if (backend is not null)
        {
            settings["Gert:Sandbox:Backend"] = backend;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGertExternal(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Default_backend_is_monty()
    {
        using var provider = Provider(null);
        provider.GetRequiredService<ISandbox>().Should().BeOfType<MontySandbox>();
    }

    [Theory]
    [InlineData("monty")]
    [InlineData("Monty")]
    [InlineData(" monty ")]
    public void Monty_backend_resolves_monty(string value)
    {
        using var provider = Provider(value);
        provider.GetRequiredService<ISandbox>().Should().BeOfType<MontySandbox>();
    }

    [Theory]
    [InlineData("gvisor")]
    [InlineData("GViSoR")]
    public void Gvisor_backend_resolves_gvisor(string value)
    {
        using var provider = Provider(value);
        provider.GetRequiredService<ISandbox>().Should().BeOfType<GVisorSandbox>();
    }

    [Fact]
    public void Unknown_backend_fails_fast()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Gert:Sandbox:Backend"] = "docker" })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();

        services.Invoking(s => s.AddGertExternal(configuration))
            .Should().Throw<InvalidOperationException>().WithMessage("*docker*");
    }
}
