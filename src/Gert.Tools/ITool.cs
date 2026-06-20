namespace Gert.Tools;

/// <summary>
/// A model-callable tool (chat-and-tools.md section tool loop). The orchestrator
/// advertises entitled tools to the model, invokes <see cref="ExecuteAsync"/>
/// when the model emits a call, and turns the <see cref="ToolResult"/> into a
/// <c>tool_result</c> event + a persisted <c>tool_calls</c> row. Implementations
/// (RagTool, WebSearchTool, PythonSandboxTool, ...) call the external
/// ports -- they hold no transport or persistence of their own here.
/// </summary>
public interface ITool
{
    /// <summary>
    /// Capability id used by the registry, the <c>gert_tools</c> entitlement, and the
    /// recorded <c>tool_calls.kind</c> (e.g. <c>rag</c>). This is the single identity
    /// of the tool - there is no separate enum.
    /// </summary>
    string Id { get; }

    /// <summary>Model-facing function name (e.g. <c>search_documents</c>).</summary>
    string Name { get; }

    /// <summary>Human/model-readable description advertised to the model.</summary>
    string Description { get; }

    /// <summary>JSON-schema of the tool's parameters (as a JSON string).</summary>
    string ParametersSchema { get; }

    /// <summary>
    /// The tool's execution flow - the single axis the runner dispatches on
    /// (<see cref="ToolType.Modal"/> is timeout-exempt). Defaults to
    /// <see cref="ToolType.Standard"/>; a modal tool overrides it.
    /// </summary>
    ToolType Type => ToolType.Standard;

    /// <summary>
    /// Whether the tool needs a human in the loop (ask_user). An autonomous driver
    /// (sub-agent, headless - <see cref="IToolHost.Ui"/> is null) excludes such tools
    /// at advertise time and fails them closed at execution. Defaults to false.
    /// </summary>
    bool RequiresHuman => false;

    /// <summary>Display title for the tools menu (chat-and-tools.md section tool catalog). Defaults to <see cref="Name"/>.</summary>
    string Title => Name;

    /// <summary>Icon key into the curated client vocabulary (icons.ts). Defaults to a generic tool glyph.</summary>
    string Icon => "tool";

    /// <summary>The menu grouping/source the descriptor sorts under. Defaults to the built-in group.</summary>
    string Group => "builtin";

    /// <summary>
    /// Execute the tool against the <paramref name="host"/> capability surface for the
    /// given arguments (the model's tool-call payload). The host is pre-scoped to the
    /// active (user, project[, conversation]); the tool never sees iss/sub.
    /// </summary>
    Task<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        IToolHost host,
        CancellationToken cancellationToken = default);
}
