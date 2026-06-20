using Gert.Tools.Hosting;

namespace Gert.Tools;

/// <summary>
/// A <see cref="ToolType.Standard"/> tool with strongly-typed arguments AND result
/// (chat-and-tools.md section tool loop). The concrete <c>ToolCall&lt;TArgs, TResult&gt;</c> base
/// (in the impl leaf) bridges the non-generic <see cref="ITool.ExecuteAsync"/> to
/// <see cref="CallAsync"/>: it deserializes the model's <see cref="ToolInvocation.ArgumentsJson"/>
/// into <typeparamref name="TArgs"/> and runs the registered <c>IValidator&lt;TArgs&gt;</c>, so a
/// parse or validation failure becomes a model-correctable tool error (the model retries) before
/// <see cref="CallAsync"/> ever runs. Keeping <see cref="ITool"/> non-generic keeps the runner
/// reflection-free.
/// </summary>
/// <typeparam name="TArgs">The tool's argument record (a validated DTO).</typeparam>
/// <typeparam name="TResult">The tool's result payload type.</typeparam>
public interface IToolCall<TArgs, TResult> : ITool
{
    /// <summary>
    /// Run the tool against already-parsed, already-validated <paramref name="args"/>. The base
    /// maps the returned <see cref="ToolCallResult{TResult}"/> back to a <see cref="ToolResult"/>.
    /// </summary>
    Task<ToolCallResult<TResult>> CallAsync(
        TArgs args,
        ToolInvocation invocation,
        IToolHost host,
        CancellationToken cancellationToken = default);
}
