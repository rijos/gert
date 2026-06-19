using FluentAssertions;
using FluentValidation;
using Gert.Chat;
using Gert.Database;
using Gert.Model.Dtos;
using Gert.Rag;
using Gert.Service;
using Gert.Service.External;
using Gert.Service.Tools;
using Gert.Storage;
using Gert.Testing.Fakes;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Gert.Tools.Tests;

/// <summary>
/// Hand-sync guard for the tool census (auth.md section tool registry): the id-only
/// <see cref="ToolRegistry"/> (built from <c>BuiltInToolIds</c> in Gert.Service) and the
/// <c>AddBuiltinTools</c> <see cref="ITool"/> registrations (Gert.Tools adapter) are two
/// lists that MUST agree - a tool registered without its id (or vice versa) silently
/// breaks entitlement or execution. Resolves both from the production wiring and asserts
/// set-equality.
/// </summary>
public sealed class ToolRegistrationTests
{
    private static ServiceProvider BuildProductionHost()
    {
        var services = new ServiceCollection();
        services.AddGertServices();
        services.AddBuiltinTools();

        // The host-supplied ports the tools ctor-inject - substitutes suffice;
        // nothing executes here. (IObjectStore feeds the MemoryService that
        // SaveMemoryTool wraps.)
        services.AddSingleton(Substitute.For<IChatDatabaseProvider>());
        services.AddSingleton(Substitute.For<IRagIndexProvider>());
        services.AddSingleton(Substitute.For<IEmbeddingClient>());
        services.AddSingleton(Substitute.For<IWebSearch>());
        services.AddSingleton(Substitute.For<IWebFetcher>());
        services.AddSingleton(Substitute.For<IPythonSandbox>());
        services.AddSingleton(Substitute.For<IChatClientFactory>());
        // SubAgentTool is the one tool that logs (its nested loop degrades
        // failures); hosts always carry logging.
        services.AddLogging();
        services.AddSingleton(Substitute.For<Gert.Storage.IObjectStore>());
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

        toolIds.Should().BeEquivalentTo(
            registry.AllIds,
            "BuiltInToolIds and AddTools are hand-synced lists - they must name the same capability ids");
    }

    [Theory]
    [InlineData("ask_user")]
    [InlineData("fetch")]
    [InlineData("memory")]
    [InlineData("sub_agent")]
    public void The_new_tool_is_a_registered_capability(string id)
    {
        using var sp = BuildProductionHost();

        sp.GetRequiredService<ToolRegistry>().Contains(id).Should().BeTrue();
    }

    [Theory]
    [InlineData("ask_user")]
    [InlineData("fetch")]
    [InlineData("memory")]
    [InlineData("sub_agent")]
    public void Tool_toggles_accept_the_new_id(string id)
    {
        using var sp = BuildProductionHost();
        var validator = sp.GetRequiredService<IValidator<ToolToggles>>();

        validator.Validate(new ToolToggles(new Dictionary<string, bool> { [id] = true }))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void Tool_toggles_still_reject_a_typo()
    {
        using var sp = BuildProductionHost();
        var validator = sp.GetRequiredService<IValidator<ToolToggles>>();

        validator.Validate(new ToolToggles(new Dictionary<string, bool> { ["ask_userr"] = true }))
            .IsValid.Should().BeFalse();
    }
}
