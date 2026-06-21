using Gert.Tools;

namespace Gert.Agent.Loop;

/// <summary>
/// One offered tool resolved for the run (a <see cref="Toolset"/> entry): the <see cref="Tool"/>,
/// whether the turn is <see cref="Entitled"/> to run it (the plan-time ceiling), and its
/// <see cref="Effective"/> bounds (the tool's intrinsic <c>ToolBounds</c> with operator overrides
/// applied). <see cref="Kind"/> is the recorded <c>tool_calls.kind</c> = the tool's id.
/// </summary>
public sealed record ToolEntry(ITool Tool, bool Entitled, ToolBounds Effective)
{
    /// <summary>The recorded kind / card id - the tool's single identity.</summary>
    public string Kind => Tool.Id;
}
