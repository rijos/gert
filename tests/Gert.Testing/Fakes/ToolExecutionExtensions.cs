using Gert.Tools;

namespace Gert.Testing.Fakes;

/// <summary>
/// Test-ergonomics for the <see cref="ITool.ExecuteAsync"/> capability-host signature. A two-arg
/// call routes through <see cref="FakeToolHost.Shared"/> (a do-nothing host) - faithful for tools
/// that ignore the host. A test exercising a capability calls the three-arg interface method with
/// its own configured <see cref="FakeToolHost"/>; that instance method wins overload resolution, so
/// this overload never shadows an explicit host.
/// </summary>
public static class ToolExecutionExtensions
{
    /// <summary>Execute <paramref name="tool"/> against the shared do-nothing host.</summary>
    public static Task<ToolResult> ExecuteAsync(
        this ITool tool,
        ToolInvocation invocation,
        CancellationToken cancellationToken = default) =>
        tool.ExecuteAsync(invocation, FakeToolHost.Shared, cancellationToken);
}
