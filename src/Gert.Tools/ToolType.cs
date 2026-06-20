namespace Gert.Tools;

/// <summary>
/// The mutually-exclusive execution flow of a tool (chat-and-tools.md section tool loop),
/// the single axis the runner dispatches on. Orthogonal, composable markers
/// (<see cref="IToolReminder"/>) are NOT values here.
/// </summary>
public enum ToolType
{
    /// <summary>
    /// A request/response tool: the model emits args, the tool returns a result, the loop
    /// continues. The generic <c>TurnOptions.ToolCallTimeout</c> backstop applies.
    /// </summary>
    Standard,

    /// <summary>
    /// A tool that legitimately blocks mid-turn on out-of-band input (<c>ask_user</c>) or runs
    /// a long nested flow (<c>run_sub_agent</c>). The runner exempts it from the generic
    /// <c>ToolCallTimeout</c> - it carries its own deadline math, and the turn's lifetime token
    /// stays the hard wall (chat-and-tools.md section Ask the user).
    /// </summary>
    Modal,
}
