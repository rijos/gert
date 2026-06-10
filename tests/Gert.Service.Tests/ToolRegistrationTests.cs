using FluentAssertions;
using FluentValidation;
using Gert.Database;
using Gert.Model.Dtos;
using Gert.Service.External;
using Gert.Service.Tools;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Gert.Service.Tests;

/// <summary>
/// The hand-sync guard for the tool census (auth.md § tool registry): the
/// <c>BuiltInToolIds</c> array (which builds the id-only <see cref="ToolRegistry"/>
/// singleton) and the <c>AddTools</c> registrations are two lists that MUST
/// agree — a tool registered without its id (or vice versa) silently breaks
/// either entitlement or execution. This resolves both from the production
/// <c>AddGertServices</c> wiring and asserts set-equality, killing the "keep in
/// sync" comment's risk for good.
/// </summary>
public sealed class ToolRegistrationTests
{
    private static ServiceProvider BuildProductionHost()
    {
        var services = new ServiceCollection();
        services.AddGertServices();

        // The host-supplied ports the tools ctor-inject — substitutes suffice;
        // nothing executes here.
        services.AddSingleton(Substitute.For<IChatDatabaseProvider>());
        services.AddSingleton(Substitute.For<IRagDatabaseProvider>());
        services.AddSingleton(Substitute.For<IEmbeddingClient>());
        services.AddSingleton(Substitute.For<IWebSearch>());
        services.AddSingleton(Substitute.For<ISandbox>());
        services.AddScoped<IUserContext>(_ => new TestUserContext());

        return services.BuildServiceProvider();
    }

    [Fact]
    public void The_registered_tools_match_the_built_in_id_registry_exactly()
    {
        using var sp = BuildProductionHost();
        using var scope = sp.CreateScope();

        var toolIds = scope.ServiceProvider.GetRequiredService<IEnumerable<ITool>>()
            .Select(t => t.Id)
            .ToHashSet(StringComparer.Ordinal);
        var registry = sp.GetRequiredService<ToolRegistry>();

        toolIds.Should().BeEquivalentTo(registry.AllIds,
            "BuiltInToolIds and AddTools are hand-synced lists — they must name the same capability ids");
    }

    [Fact]
    public void Ask_user_is_a_registered_capability()
    {
        using var sp = BuildProductionHost();

        sp.GetRequiredService<ToolRegistry>().Contains("ask_user").Should().BeTrue();
    }

    [Fact]
    public void Tool_toggles_accept_the_new_id_and_still_reject_a_typo()
    {
        using var sp = BuildProductionHost();
        var validator = sp.GetRequiredService<IValidator<ToolToggles>>();

        validator.Validate(new ToolToggles(new Dictionary<string, bool> { ["ask_user"] = true }))
            .IsValid.Should().BeTrue();

        validator.Validate(new ToolToggles(new Dictionary<string, bool> { ["ask_userr"] = true }))
            .IsValid.Should().BeFalse();
    }
}
