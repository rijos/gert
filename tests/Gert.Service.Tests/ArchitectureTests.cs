using Microsoft.Extensions.DependencyInjection;
using NetArchTest.Rules;
using Xunit;

namespace Gert.Service.Tests;

/// <summary>
/// Enforces the inward-only reference direction from docs/design/tech-stack.md section
/// Architecture: the host-agnostic service layer must never depend on a host or adapter, so the
/// services stay drivable from any host.
/// </summary>
public class ArchitectureTests
{
    private static readonly System.Reflection.Assembly ServiceAssembly =
        typeof(global::Gert.Service.IUserContext).Assembly;

    [Fact]
    public void Service_does_not_depend_on_hosts_or_adapters()
    {
        var result = Types.InAssembly(ServiceAssembly)
            .Should()
            .NotHaveDependencyOnAny(
                "Gert.Api",
                // The turn/agent execution engine sits OUTWARD of the service layer
                // (host -> Gert.Agent -> Gert.Service); the service layer keeps only the
                // request-facing read side (the bus + conversation reader/streamer), so it
                // must never reference the engine back.
                "Gert.Agent",
                "Gert.Authentication",
                // Capability CONTRACTS (Gert.Chat, Gert.Storage, Gert.Database, Gert.Rag,
                // Gert.Tools) are inward of the service layer - they hold the ports + the generic,
                // impl-agnostic catalog/factory; only the per-impl leaf assemblies are forbidden.
                "Gert.Chat.OpenAI",
                "Gert.Tools.Builtin",
                "Gert.Ingestion",
                "Gert.Storage.Local",
                "Gert.Database.Sqlite",
                "Gert.Database.Postgres",
                "Gert.Rag.Sqlite")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            "Gert.Service must not reference any host or adapter assembly. Offending types: " +
            string.Join(", ", result.FailingTypeNames ?? System.Array.Empty<string>()));
    }

    /// <summary>
    /// The tool contracts assembly (Gert.Tools: ITool / ToolRegistry / ToolResult + the
    /// web-search/fetch/sandbox ports) sits inward of the service layer, mirroring Gert.Chat. It
    /// must depend on neither its impl leaf (Gert.Tools.Builtin) nor Gert.Service, so the service
    /// layer can reference it without dragging in an adapter (PluginArchitectureTests covers the
    /// search/sandbox capability-plugin split within the impl leaf).
    /// </summary>
    [Fact]
    public void Tools_contracts_do_not_depend_on_their_impl_or_the_service_layer()
    {
        var result = Types.InAssembly(typeof(global::Gert.Tools.ITool).Assembly)
            .Should()
            .NotHaveDependencyOnAny("Gert.Tools.Builtin", "Gert.Service")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            "Gert.Tools (contracts) must not reference its impl leaf or the service layer. " +
            "Offending types: " + string.Join(", ", result.FailingTypeNames ?? System.Array.Empty<string>()));
    }

    /// <summary>
    /// The acceptance gate for Phase 6 (chat-and-tools.md section the tool loop): the tool impl leaf
    /// (Gert.Tools.Builtin) must NOT depend on the service layer. Every tool reaches RAG, objects,
    /// the UI, and delegation through the host's <see cref="global::Gert.Tools.IToolHost"/> seams at
    /// call time - never the loop impl - so the leaf sits squarely outward of Gert.Service, mirroring
    /// Gert.Chat.OpenAI -> Gert.Chat. A future edit that re-introduces the edge fails here.
    /// </summary>
    [Fact]
    public void Tools_impl_leaf_does_not_depend_on_the_service_layer()
    {
        var result = Types.InAssembly(typeof(global::Gert.Tools.Builtin.RagTool).Assembly)
            .Should()
            .NotHaveDependencyOn("Gert.Service")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            "Gert.Tools.Builtin must not reference the service layer (the tools reach RAG/objects/UI/" +
            "delegation through the host seams). Offending types: " +
            string.Join(", ", result.FailingTypeNames ?? System.Array.Empty<string>()));
    }

    /// <summary>
    /// The turn/agent execution engine (Gert.Agent: worker, queue, planner, runner, AgentLoop,
    /// the chat tool-host wiring) is the layer between the host and the service layer
    /// (host -> Gert.Agent -> Gert.Service). It may reference the service layer + the capability
    /// CONTRACTS it drives, but never the host (Gert.Api) nor an adapter IMPL leaf - those wire
    /// in at the composition root. A future edit that drags an adapter or the host into the engine
    /// fails here.
    /// </summary>
    [Fact]
    public void Agent_engine_does_not_depend_on_the_host_or_adapter_impls()
    {
        var result = Types.InAssembly(System.Reflection.Assembly.Load("Gert.Agent"))
            .Should()
            .NotHaveDependencyOnAny(
                "Gert.Api",
                "Gert.Authentication",
                "Gert.Chat.OpenAI",
                "Gert.Tools.Builtin",
                "Gert.Ingestion",
                "Gert.Storage.Local",
                "Gert.Database.Sqlite",
                "Gert.Database.Postgres",
                "Gert.Rag.Sqlite")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            "Gert.Agent must not reference the host or an adapter impl leaf (it may reference " +
            "Gert.Service + the capability contracts). Offending types: " +
            string.Join(", ", result.FailingTypeNames ?? System.Array.Empty<string>()));
    }

    /// <summary>
    /// Per-user isolation (principles.md) requires that any service consuming the
    /// request-scoped <see cref="IUserContext"/> is itself scoped: a singleton would
    /// capture the first caller's identity and then serve their data to everyone (a
    /// captive-dependency cross-user leak). This pins the service layer's own
    /// registrations - the <c>AddUserScoped</c> contract in
    /// <see cref="ServiceCollectionExtensions"/> - so a future edit that flips one to
    /// singleton fails here, not in production. The host's <c>ValidateScopes</c> is the
    /// runtime backstop for registrations this test can't see (factory singletons that
    /// capture IUserContext via the provider, and host/adapter registrations).
    /// </summary>
    [Fact]
    public void Services_consuming_IUserContext_are_scoped()
    {
        var services = new ServiceCollection();
        services.AddGertServices();

        var offenders = services
            .Where(d => d.Lifetime != ServiceLifetime.Scoped)
            .Where(d => d.ImplementationType is not null && ConsumesUserContext(d.ImplementationType))
            .Select(d => $"{d.ImplementationType!.Name} ({d.Lifetime})")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "These services take IUserContext but are not scoped - a non-scoped consumer of " +
            "the per-request user context leaks one caller's identity across requests. Register " +
            "them with AddUserScoped. Offenders: " + string.Join(", ", offenders));
    }

    /// <summary>True if any public constructor of <paramref name="type"/> takes an
    /// <see cref="IUserContext"/> directly (the reviewable, declared dependency).</summary>
    private static bool ConsumesUserContext(System.Type type) =>
        type.GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Any(p => p.ParameterType == typeof(global::Gert.Service.IUserContext));
}
