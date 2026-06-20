namespace Gert.Tools;

/// <summary>
/// The capability surface a tool is HANDED at invocation (chat-and-tools.md section tool host).
/// Built per-call at the composition root, pre-scoped to the active (user, project[, conversation]):
/// a tool reads/writes objects, searches RAG, asks the user, and delegates nested work WITHOUT ever
/// seeing iss/sub - identity is the host's, never the tool's. This is the seam MCP also leaves open.
/// </summary>
public interface IToolHost
{
    /// <summary>Stored-object + RAG resources, pre-scoped to the active project/conversation.</summary>
    IToolResources Resources { get; }

    /// <summary>
    /// Human interaction (ask_user). Null on an autonomous driver (sub-agent, headless), where a
    /// <see cref="ITool.RequiresHuman"/> tool is neither advertised nor executed.
    /// </summary>
    IToolUi? Ui { get; }

    /// <summary>Delegation to a nested agent loop (run_sub_agent).</summary>
    IToolDelegate Delegate { get; }

    /// <summary>The per-invocation budget (deadline, token allowance) the tool honours.</summary>
    ToolLimits Limits { get; }
}
