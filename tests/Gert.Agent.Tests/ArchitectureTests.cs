using Microsoft.Extensions.DependencyInjection;
using NetArchTest.Rules;
using Xunit;

namespace Gert.Agent.Tests;

/// <summary>
/// Pins the turn/agent execution engine's place in the inward-only reference direction
/// (docs/design/tech-stack.md section Architecture): host -> Gert.Agent -> Gert.Service.
/// These rules live with Gert.Agent (this project references it) so the engine's boundary
/// is asserted alongside its tests.
/// </summary>
public class ArchitectureTests
{
    private static readonly System.Reflection.Assembly AgentAssembly =
        typeof(global::Gert.Agent.TurnRunner).Assembly;

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
        var result = Types.InAssembly(AgentAssembly)
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
                "Gert.Rag.Sqlite",
                "Gert.TurnControl.Local")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            "Gert.Agent must not reference the host or an adapter impl leaf (it may reference " +
            "Gert.Service + the capability contracts). Offending types: " +
            string.Join(", ", result.FailingTypeNames ?? System.Array.Empty<string>()));
    }

    /// <summary>
    /// Per-user isolation (principles.md) requires that any engine service consuming the
    /// request-scoped <see cref="global::Gert.Service.IUserContext"/> is itself scoped: a
    /// singleton would capture the first caller's identity and then serve their data to
    /// everyone (a captive-dependency cross-user leak). This pins <c>AddGertAgent</c>'s
    /// registrations - the planner/runner are the caller-bound scoped consumers - so a future
    /// edit that flips one to singleton fails here, not in production. The host's
    /// <c>ValidateScopes</c> is the runtime backstop for registrations this test can't see.
    /// </summary>
    [Fact]
    public void Services_consuming_IUserContext_are_scoped()
    {
        var services = new ServiceCollection();
        services.AddGertAgent();

        var offenders = services
            .Where(d => d.Lifetime != ServiceLifetime.Scoped)
            .Where(d => d.ImplementationType is not null && ConsumesUserContext(d.ImplementationType))
            .Select(d => $"{d.ImplementationType!.Name} ({d.Lifetime})")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "These engine services take IUserContext but are not scoped - a non-scoped consumer of " +
            "the per-request user context leaks one caller's identity across requests. Register " +
            "them scoped. Offenders: " + string.Join(", ", offenders));
    }

    /// <summary>True if any public constructor of <paramref name="type"/> takes an
    /// <see cref="global::Gert.Service.IUserContext"/> directly (the reviewable, declared dependency).</summary>
    private static bool ConsumesUserContext(System.Type type) =>
        type.GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Any(p => p.ParameterType == typeof(global::Gert.Service.IUserContext));
}
