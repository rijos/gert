namespace Gert.Service.Tools;

/// <summary>
/// Marker for tools that legitimately block on the user mid-turn
/// (<see cref="AskUserTool"/>). The runner exempts them from the generic
/// <c>TurnOptions.ToolCallTimeout</c> backstop - a 60 s cap would kill every
/// wait - because such a tool carries its own deadline math (the
/// <see cref="ToolInvocation.Deadline"/> budget) and the turn's lifetime token
/// remains the hard wall (chat-and-tools.md section Ask the user).
/// </summary>
public interface IInteractiveTool;
