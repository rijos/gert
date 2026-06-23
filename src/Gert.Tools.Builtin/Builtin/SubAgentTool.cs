using Gert.Tools;
using Gert.Tools.Args;
using Gert.Tools.Hosting;
using Gert.Tools.Results;
using Gert.Validation;

namespace Gert.Tools.Builtin;

/// <summary>
/// The sub-agent tool (chat-and-tools.md section sub-agent). Model function <c>run_sub_agent</c>
/// delegates a self-contained task to a FRESH nested conversation through the host's
/// <see cref="IToolDelegate"/>; only the sub-agent's final text returns, so the nested rounds never
/// enter the parent history (a context-hungry side quest costs one tool result, not the whole
/// transcript). The typed-args base (<see cref="ToolCallModal{TArgs, TResult}"/>) parses + bounds
/// the <see cref="SubAgentArgs"/> (the caps live in <c>SubAgentArgsValidator</c>, so model-correctable
/// errors never reach the delegate); the nested loop, the delegable-tool intersection, and the
/// autonomous host live behind the delegate (the chat driver's <c>ChatToolDelegate</c>), so this
/// tool depends on the contract, not the loop.
///
/// <para>
/// <see cref="ToolType.Modal"/> (via <see cref="ToolCallModal{TArgs, TResult}"/>) exempts the wait
/// from the per-tool <c>ToolBounds.CallTimeout</c> backstop (a delegated research task legitimately
/// outlives 60 s); the turn's lifetime token remains the hard wall.
/// </para>
/// </summary>
public sealed class SubAgentTool : ToolCallModal<SubAgentArgs, SubAgentResult>
{
    /// <param name="validation">The fail-closed provider the base uses to prove args.</param>
    public SubAgentTool(IValidationProvider validation)
        : base(validation)
    {
    }

    /// <inheritdoc />
    public override string Id => "sub_agent";

    /// <inheritdoc />
    public override string Name => "run_sub_agent";

    /// <inheritdoc />
    public override string Title => "Sub-agents";

    /// <inheritdoc />
    public override string Icon => "user";

    /// <inheritdoc />
    public override string Group => "standard";

    // Lean on purpose: the tools region must stay under the format-adherence
    // budget (chat-and-tools.md section tool specs are a token budget).
    /// <inheritdoc />
    public override string Description =>
        "Delegate one self-contained task to a sub-agent and wait for its result. It cannot "
        + "see this conversation (pass everything it needs in task/context); it can search "
        + "docs and the web, fetch pages, and read the clock. Only its final answer returns - "
        + "use it when the intermediate work would crowd this conversation.";

    /// <inheritdoc />
    public override async Task<ToolCallResult<SubAgentResult>> CallAsync(
        SubAgentArgs args,
        ToolInvocation invocation,
        IToolHost host,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(host);

        var result = await host.Delegate
            .RunAsync(new DelegateRequest(args.Task, args.Context), cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            return ToolCallResult<SubAgentResult>.Fail(result.Error ?? "the sub-agent failed");
        }

        return ToolCallResult<SubAgentResult>.Ok(
            new SubAgentResult { Result = result.Text, Rounds = result.Rounds },
            stdout: result.Text ?? string.Empty);
    }
}
