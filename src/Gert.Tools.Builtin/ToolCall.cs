using System.Text.Json;
using Gert.Model.Json;
using Gert.Tools;
using Gert.Tools.Hosting;
using Gert.Tools.Schema;
using Gert.Validation;

namespace Gert.Tools.Builtin;

/// <summary>
/// Base for a typed tool (arguments + result). It provides the
/// non-generic <see cref="ITool.ExecuteAsync"/> bridge so the runner stays reflection-free: it
/// deserializes the model's <see cref="ToolInvocation.ArgumentsJson"/> into
/// <typeparamref name="TArgs"/> (the snake_case wire contract), runs the registered
/// <c>IValidator&lt;TArgs&gt;</c> through the injected <see cref="IValidationProvider"/>, and turns a
/// JSON-parse or <see cref="ValidationException"/> into a model-correctable
/// <see cref="ToolResult"/> (<c>Success=false</c>) - the "auto-correct" loop where the model
/// retries with fixed args. Only on a clean parse+validate does <see cref="CallAsync"/> run; its
/// <see cref="ToolCallResult{TResult}"/> is mapped back to a <see cref="ToolResult"/>.
/// <para>
/// <see cref="Type"/> defaults to <see cref="ToolType.Standard"/> but is virtual: a modal tool
/// derives from <see cref="ToolCallModal{TArgs, TResult}"/> (which overrides it to
/// <see cref="ToolType.Modal"/>) and inherits the same typed parse/validate bridge.
/// </para>
/// <para>
/// This base lives in the impl leaf (not the contracts assembly) precisely because it depends on
/// <see cref="IValidationProvider"/> - keeping the contracts (<see cref="IToolCall{TArgs, TResult}"/>,
/// <see cref="ToolCallResult{TResult}"/>) free of any Gert.Validation reference.
/// </para>
/// </summary>
/// <typeparam name="TArgs">The tool's argument record (with a registered validator).</typeparam>
/// <typeparam name="TResult">The tool's result payload type.</typeparam>
public abstract class ToolCall<TArgs, TResult> : IToolCall<TArgs, TResult>
{
    private readonly IValidationProvider _validation;

    /// <param name="validation">The fail-closed provider; the derived tool injects it.</param>
    protected ToolCall(IValidationProvider validation) =>
        _validation = validation ?? throw new ArgumentNullException(nameof(validation));

    /// <inheritdoc />
    public abstract string Id { get; }

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract string Description { get; }

    /// <inheritdoc />
    /// <remarks>
    /// GENERATED from <typeparamref name="TArgs"/> + its <c>[ToolParameter*]</c> annotations
    /// (chat-and-tools.md section "tool specs are a token budget"), so a typed tool's
    /// model-facing schema is derived from the record shape, not a hand-written string that
    /// can drift. Cached per type by <see cref="ToolSchema"/>.
    /// </remarks>
    public virtual string ParametersSchema => ToolSchema.Generate(typeof(TArgs));

    /// <summary>The tool kind. Re-declared as a virtual class member (the <see cref="ITool"/> default isn't) so a modal tool can override it; defaults to <see cref="ToolType.Standard"/>.</summary>
    public virtual ToolType Type => ToolType.Standard;

    /// <summary>Whether the tool needs a human in the loop (<c>ask_user</c>). Virtual class member (the <see cref="ITool"/> default isn't), defaults to false.</summary>
    public virtual bool RequiresHuman => false;

    /// <summary>Menu title. Re-declared as a virtual class member (the <see cref="ITool"/> default isn't); defaults to <see cref="Name"/>.</summary>
    public virtual string Title => Name;

    /// <summary>Icon key into the curated client vocabulary; virtual, defaults to the neutral glyph (matches ToolIcons.Fallback).</summary>
    public virtual string Icon => "gear";

    /// <summary>Menu grouping; virtual, defaults to the built-in group.</summary>
    public virtual string Group => "builtin";

    /// <summary>Per-turn budget ceiling; virtual class member (the <see cref="ITool"/> default isn't), defaults to <see cref="ToolBounds.Default"/>.</summary>
    public virtual ToolBounds Bounds => ToolBounds.Default;

    /// <inheritdoc />
    public abstract Task<ToolCallResult<TResult>> CallAsync(
        TArgs args,
        ToolInvocation invocation,
        IToolHost host,
        CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        IToolHost host,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        TArgs? args;
        try
        {
            args = JsonSerializer.Deserialize<TArgs>(invocation.ArgumentsJson, GertJsonOptions.Default);
        }
        catch (JsonException ex)
        {
            return new ToolResult { Success = false, Error = $"invalid arguments: {ex.Message}" };
        }

        if (args is null)
        {
            return new ToolResult { Success = false, Error = "invalid arguments: expected a JSON object" };
        }

        TArgs validated;
        try
        {
            validated = _validation.Prove(args).Value;
        }
        catch (ValidationException ex)
        {
            // Model-correctable: the model can fix the args and retry next round.
            return new ToolResult { Success = false, Error = ex.Message };
        }

        var result = await CallAsync(validated, invocation, host, cancellationToken).ConfigureAwait(false);

        // Push the tool's side-effects to the host card seam (decisions #13): the typed
        // ToolCallResult still DECLARES them (so each tool + its unit tests stay simple), and this
        // one bridge routes them to the card the driver persists/renders - the model-facing
        // ToolResult carries only the payload.
        Emit(host.Card, result);

        return new ToolResult
        {
            Success = result.Success,
            Error = result.Error,
            // Serialize whenever a typed value is present - a FAILED call may still carry one (the
            // sandbox ships its exit_code/stderr payload on a non-zero exit or timeout), so this is
            // gated on the value, not on Success.
            ResultJson = result.Value is not null
                ? JsonSerializer.Serialize(result.Value, GertJsonOptions.Default)
                : null,
        };
    }

    private static void Emit(IToolCard card, ToolCallResult<TResult> result)
    {
        if (result.Citations.Count > 0)
        {
            card.ReportCitations(result.Citations);
        }

        if (result.Stdout is { Length: > 0 } stdout)
        {
            card.ReportStdout(stdout);
        }

        if (result.Todos is { Count: > 0 } todos)
        {
            card.ReportTodos(todos);
        }

        if (result.Artifacts is { Count: > 0 } artifacts)
        {
            foreach (var artifact in artifacts)
            {
                card.ReportArtifact(artifact);
            }
        }
    }
}
