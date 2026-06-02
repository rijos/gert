using Gert.Model.Chat;

namespace Gert.Service.Tools;

/// <summary>
/// A model-callable tool (chat-and-tools.md § tool loop). The orchestrator
/// advertises entitled tools to the model, invokes <see cref="ExecuteAsync"/>
/// when the model emits a call, and turns the <see cref="ToolResult"/> into a
/// <c>tool_result</c> event + a persisted <c>tool_calls</c> row. Implementations
/// (RagTool, WebSearchTool, SandboxTool) live in U7c and call the U2 external
/// ports — they hold no transport or persistence of their own here.
/// </summary>
public interface ITool
{
    /// <summary>
    /// Capability id used by the registry, the <c>gert_tools</c> entitlement, and the
    /// recorded <c>tool_calls.kind</c> (e.g. <c>rag</c>). This is the single identity
    /// of the tool — there is no separate enum.
    /// </summary>
    string Id { get; }

    /// <summary>Model-facing function name (e.g. <c>search_documents</c>).</summary>
    string Name { get; }

    /// <summary>Human/model-readable description advertised to the model.</summary>
    string Description { get; }

    /// <summary>JSON-schema of the tool's parameters (as a JSON string).</summary>
    string ParametersSchema { get; }

    /// <summary>
    /// Execute the tool against the active project's resources for the given
    /// arguments (the model's tool-call payload).
    /// </summary>
    Task<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One tool call from the model — the active project and the raw JSON arguments
/// (e.g. <c>{"query":"…","k":8}</c>).
/// </summary>
public sealed record ToolInvocation
{
    public required string Pid { get; init; }

    /// <summary>Raw tool arguments as a JSON string.</summary>
    public required string ArgumentsJson { get; init; }
}

/// <summary>
/// A tool's outcome — what the orchestrator feeds back to the model and renders
/// as a <c>tool_result</c>. <see cref="ResultJson"/> is the opaque payload;
/// <see cref="Citations"/> seeds the message footnotes (RAG / web hits).
/// </summary>
public sealed record ToolResult
{
    public required bool Success { get; init; }

    /// <summary>Result payload as JSON (hits / results / stdout).</summary>
    public string? ResultJson { get; init; }

    /// <summary>Citations derived from this result, if any.</summary>
    public IReadOnlyList<Citation> Citations { get; init; } = [];

    /// <summary>Error message when <see cref="Success"/> is false.</summary>
    public string? Error { get; init; }
}
