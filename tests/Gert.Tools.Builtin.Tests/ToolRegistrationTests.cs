using FluentAssertions;
using FluentValidation;
using Gert.Chat;
using Gert.Database;
using Gert.Model.Dtos;
using Gert.Rag;
using Gert.Service;
using Gert.Storage;
using Gert.Testing.Fakes;
using Gert.Tools;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Gert.Tools.Builtin.Tests;

/// <summary>
/// Invariants of the tool registry (auth.md section tool registry): the id-only
/// <see cref="ToolRegistry"/> singleton is DERIVED from the registered <see cref="ITool"/>
/// instances (no hand-maintained census), so the only thing to guard is that the derivation
/// holds - the registry's ids equal the resolved tools' ids with no duplicates - and that the
/// registry's ctors reject a duplicate id rather than silently coalescing it (which would split
/// a capability key across two tools and break entitlement/execution).
/// </summary>
public sealed class ToolRegistrationTests
{
    private static ServiceProvider BuildProductionHost()
    {
        var services = new ServiceCollection();
        services.AddGertServices();
        services.AddBuiltinTools();

        // The host-supplied ports the tools ctor-inject - substitutes suffice;
        // nothing executes here.
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
    public void The_id_registry_is_derived_from_the_registered_tools_with_no_duplicates()
    {
        using var sp = BuildProductionHost();
        using var scope = sp.CreateScope();

        var toolIds = scope.ServiceProvider.GetRequiredService<IEnumerable<ITool>>()
            .Select(t => t.Id)
            .ToList();
        var registry = sp.GetRequiredService<ToolRegistry>();

        // No tool id collides (derivation would have thrown, but pin it explicitly).
        registry.AllIds.Should().HaveCount(toolIds.Count, "every registered tool contributes a distinct capability id");
        registry.AllIds.Should().BeEquivalentTo(
            toolIds,
            "the id-only registry is derived from the registered ITool instances");
    }

    [Fact]
    public void The_id_only_ctor_rejects_a_duplicate_id()
    {
        var act = () => new ToolRegistry(new[] { "a", "a" });

        act.Should().Throw<ArgumentException>().WithMessage("*duplicate*'a'*");
    }

    [Fact]
    public void The_instance_ctor_rejects_two_tools_sharing_an_id()
    {
        var act = () => new ToolRegistry(new ITool[] { new FakeTool("dup"), new FakeTool("dup") });

        act.Should().Throw<ArgumentException>().WithMessage("*duplicate*'dup'*");
    }

    [Theory]
    [InlineData("ask_user")]
    [InlineData("fetch")]
    [InlineData("sub_agent")]
    public void The_new_tool_is_a_registered_capability(string id)
    {
        using var sp = BuildProductionHost();

        sp.GetRequiredService<ToolRegistry>().Contains(id).Should().BeTrue();
    }

    [Theory]
    [InlineData("ask_user")]
    [InlineData("fetch")]
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

    // Minimal ITool stub: the instance ctor keys only on Id, so the rest is unreachable.
    private sealed class FakeTool(string id) : ITool
    {
        public string Id => id;

        public string Name => id;

        public string Description => id;

        public string ParametersSchema => "{}";

        public Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            IToolHost host,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
