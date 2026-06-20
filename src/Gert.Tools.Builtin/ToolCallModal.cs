using Gert.Tools;

namespace Gert.Tools.Builtin;

/// <summary>
/// Base for a <see cref="ToolType.Modal"/> tool - one that legitimately blocks mid-turn on
/// out-of-band input (<c>ask_user</c>) or runs a long nested flow (<c>run_sub_agent</c>). Setting
/// <see cref="ITool.Type"/> to <see cref="ToolType.Modal"/> is the single signal the runner keys
/// off to exempt the call from the generic <c>ToolCallTimeout</c>: the tool owns its own deadline
/// math (the <see cref="ToolInvocation.Deadline"/> budget) and the turn's lifetime token stays the
/// hard wall (chat-and-tools.md section Ask the user). Replaces the old <c>IInteractiveTool</c>
/// marker. The derived tool still implements <see cref="ITool.ExecuteAsync"/> itself - modal flows
/// differ too much (a question-wait vs a nested model loop) to share a body.
/// </summary>
public abstract class ToolCallModal : ITool
{
    /// <inheritdoc />
    public abstract string Id { get; }

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract string Description { get; }

    /// <inheritdoc />
    public abstract string ParametersSchema { get; }

    /// <inheritdoc />
    public ToolType Type => ToolType.Modal;

    /// <inheritdoc />
    public abstract Task<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken = default);
}
