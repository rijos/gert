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
}
