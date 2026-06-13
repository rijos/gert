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
                "Gert.External",
                "Gert.Database.Sqlite",
                "Gert.Database.Postgres")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            "Gert.Service must not reference any host or adapter assembly. Offending types: " +
            string.Join(", ", result.FailingTypeNames ?? System.Array.Empty<string>()));
    }
}
