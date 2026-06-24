using System.Text.Json;
using Gert.Tools;
using Microsoft.Extensions.AI;

namespace Gert.Agent.Loop;

/// <summary>
/// The advertise-time Microsoft.Extensions.AI <see cref="AIFunction"/> for a Gert <see cref="ITool"/>
/// (decisions #13): it carries the tool's model-facing name, description, and - crucially - its LEAN
/// JSON schema verbatim (<see cref="ITool.ParametersSchema"/>, the compact snake_case shape
/// <c>ToolSchema</c> generates), NOT the verbose schema <c>AIFunctionFactory.CreateDeclaration</c>
/// would synthesise. The tools region is a token budget (chat-and-tools.md section tool specs;
/// qwen format adherence collapses past ~1.8k tokens), so the advertised schema must stay exactly
/// what Gert produced.
///
/// <para>
/// Dispatch is the loop's job, not this function's: the agent loop resolves the model's call name to
/// its <see cref="Toolset"/> entry and runs the <see cref="ITool"/> under the entitlement re-check +
/// per-tool budgets + timeout, then folds the tool's reported side-effects off the per-call
/// <see cref="ToolCardCollector"/>. So <see cref="InvokeCoreAsync"/> is never called - it exists only
/// to satisfy the abstract base.
/// </para>
/// </summary>
internal sealed class ToolFunction : AIFunction
{
    private readonly ITool _tool;

    public ToolFunction(ITool tool)
    {
        _tool = tool ?? throw new ArgumentNullException(nameof(tool));
        JsonSchema = ParseSchema(tool.ParametersSchema);
    }

    /// <inheritdoc />
    public override string Name => _tool.Name;

    /// <inheritdoc />
    public override string Description => _tool.Description;

    /// <inheritdoc />
    public override JsonElement JsonSchema { get; }

    /// <inheritdoc />
    protected override ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "A Gert tool is dispatched by the agent loop (entitlement, budgets, timeout, card), not invoked directly.");

    /// <summary>
    /// Parse a tool's parameter-schema string into a <see cref="JsonElement"/> verbatim. A
    /// malformed/empty schema degrades to an empty object schema rather than throwing - a bad tool
    /// spec must not crash the whole turn.
    /// </summary>
    private static JsonElement ParseSchema(string schema)
    {
        if (!string.IsNullOrWhiteSpace(schema))
        {
            try
            {
                using var doc = JsonDocument.Parse(schema);
                return doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                // fall through to the empty object schema
            }
        }

        using var fallback = JsonDocument.Parse("""{"type":"object"}""");
        return fallback.RootElement.Clone();
    }
}
