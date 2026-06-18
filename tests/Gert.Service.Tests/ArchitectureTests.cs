using Microsoft.Extensions.DependencyInjection;
using NetArchTest.Rules;
using Xunit;

namespace Gert.Service.Tests;

/// <summary>
/// Enforces the inward-only reference direction from
/// docs/design/tech-stack.md section Architecture: the host-agnostic service layer
/// must never depend on a host or adapter. This is the structural guarantee
/// that keeps the services drivable from any host - compiler- and
/// test-enforced from day one.
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
                "Gert.Authentication",
                // Capability CONTRACTS (Gert.Chat, Gert.Storage, Gert.Database, Gert.Rag) are
                // inward of the service layer - they hold the ports + the generic, impl-agnostic
                // catalog/factory; only the per-impl leaf assemblies are forbidden.
                "Gert.Chat.OpenAI",
                "Gert.Tools",
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
