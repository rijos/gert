using Gert.Model.Chat;
using Gert.Tools;

namespace Gert.Testing.Fakes;

/// <summary>
/// Test-ergonomics for the <see cref="ITool.ExecuteAsync"/> capability-host signature. A two-arg
/// call routes through <see cref="FakeToolHost.Shared"/> (a do-nothing host) - faithful for tools
/// that ignore the host. A test exercising a capability calls the three-arg interface method with
/// its own configured <see cref="FakeToolHost"/>; that instance method wins overload resolution, so
/// this overload never shadows an explicit host.
///
/// <para>
/// Side-effects no longer ride <see cref="ToolResult"/> (they go through <see cref="Hosting.IToolCard"/>),
/// so <see cref="RunAsync"/> runs a tool against a fresh-or-given <see cref="FakeToolHost"/> and folds
/// the slim result together with that host's captured card into one <see cref="ToolRun"/> - the shape
/// the tool tests assert on (Success/ResultJson/Error + Citations/Stdout/Todos/Artifacts).
/// </para>
/// </summary>
public static class ToolExecutionExtensions
{
    /// <summary>Execute <paramref name="tool"/> against the shared do-nothing host.</summary>
    public static Task<ToolResult> ExecuteAsync(
        this ITool tool,
        ToolInvocation invocation,
        CancellationToken cancellationToken = default) =>
        tool.ExecuteAsync(invocation, FakeToolHost.Shared, cancellationToken);

    /// <summary>
    /// Run <paramref name="tool"/> against <paramref name="host"/> (a fresh <see cref="FakeToolHost"/>
    /// if none is given) and return the model-facing result combined with the side-effects the tool
    /// reported to that host's card.
    /// </summary>
    public static async Task<ToolRun> RunAsync(
        this ITool tool,
        ToolInvocation invocation,
        FakeToolHost? host = null,
        CancellationToken cancellationToken = default)
    {
        host ??= new FakeToolHost();
        var result = await tool.ExecuteAsync(invocation, host, cancellationToken).ConfigureAwait(false);
        return new ToolRun(
            result.Success,
            result.ResultJson,
            result.Error,
            host.Captured.Citations,
            host.Captured.Stdout,
            host.Captured.Todos,
            host.Captured.Artifacts);
    }
}
