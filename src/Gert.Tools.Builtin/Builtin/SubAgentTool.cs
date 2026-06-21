using System.Text.Json;
using Gert.Tools;
using Gert.Tools.Hosting;

namespace Gert.Tools.Builtin;

/// <summary>
/// The sub-agent tool (chat-and-tools.md section sub-agent). Model function <c>run_sub_agent</c>
/// delegates a self-contained task to a FRESH nested conversation through the host's
/// <see cref="IToolDelegate"/>; only the sub-agent's final text returns, so the nested rounds never
/// enter the parent history (a context-hungry side quest costs one tool result, not the whole
/// transcript). The tool parses + bounds the model's arguments (model-correctable errors stay here);
/// the nested loop, the delegable-tool intersection, and the autonomous host live behind the delegate
/// (the chat driver's <c>ChatToolDelegate</c>), so this tool depends on the contract, not the loop.
///
/// <para>
/// <see cref="ToolType.Modal"/> (via <see cref="ToolCallModal"/>) exempts the wait from the per-tool
/// <c>ToolBounds.CallTimeout</c> backstop (a delegated research task legitimately outlives 60 s); the
/// turn's lifetime token remains the hard wall.
/// </para>
/// </summary>
public sealed class SubAgentTool : ToolCallModal
{
    private const int MaxTaskChars = 8_000;
    private const int MaxContextChars = 32_000;

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
    public override string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "task": { "type": "string", "description": "The complete, self-contained task." },
            "context": { "type": "string", "description": "Optional background material the task needs." }
          },
          "required": ["task"]
        }
        """;

    /// <inheritdoc />
    public override async Task<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        IToolHost host,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(host);

        string? task;
        string? context;
        try
        {
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            task = doc.RootElement.TryGetProperty("task", out var t) ? t.GetString() : null;
            context = doc.RootElement.TryGetProperty("context", out var c) ? c.GetString() : null;
        }
        catch (JsonException ex)
        {
            return Fail($"invalid arguments: {ex.Message}");
        }

        // Model-correctable arg errors stay at the tool (the delegate re-checks too,
        // but a clear message here keeps the failure on the call that caused it).
        if (string.IsNullOrWhiteSpace(task))
        {
            return Fail("'task' is required");
        }

        if (task.Length > MaxTaskChars || (context?.Length ?? 0) > MaxContextChars)
        {
            return Fail("task or context too long");
        }

        var result = await host.Delegate
            .RunAsync(new DelegateRequest(task, context), cancellationToken)
            .ConfigureAwait(false);

        return result.Success
            ? new ToolResult
            {
                Success = true,
                ResultJson = JsonSerializer.Serialize(new { result = result.Text, rounds = result.Rounds }),
                Stdout = result.Text,
            }
            : new ToolResult { Success = false, Error = result.Error };
    }

    private static ToolResult Fail(string error) => new() { Success = false, Error = error };
}
