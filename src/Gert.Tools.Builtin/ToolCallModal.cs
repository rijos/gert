using Gert.Tools;
using Gert.Validation;

namespace Gert.Tools.Builtin;

/// <summary>
/// Base for a <see cref="ToolType.Modal"/> typed tool - one that legitimately blocks mid-turn on
/// out-of-band input (<c>ask_user</c>) or runs a long nested flow (<c>run_sub_agent</c>). It is the
/// <see cref="ToolCall{TArgs, TResult}"/> bridge (deserialize + validate <typeparamref name="TArgs"/>
/// before <see cref="ToolCall{TArgs, TResult}.CallAsync"/>) with one difference: <see cref="Type"/>
/// is <see cref="ToolType.Modal"/>. That single signal is what the runner keys off to exempt the
/// call from the per-tool <c>ToolBounds.CallTimeout</c> - the modal flow owns its own deadline math
/// (the <see cref="ToolInvocation.Deadline"/> budget) and the turn's lifetime token stays the hard
/// wall (chat-and-tools.md section Ask the user).
/// </summary>
/// <typeparam name="TArgs">The tool's argument record (with a registered validator).</typeparam>
/// <typeparam name="TResult">The tool's result payload type.</typeparam>
public abstract class ToolCallModal<TArgs, TResult> : ToolCall<TArgs, TResult>
{
    /// <param name="validation">The fail-closed provider; the derived tool injects it.</param>
    protected ToolCallModal(IValidationProvider validation)
        : base(validation)
    {
    }

    /// <inheritdoc />
    public override ToolType Type => ToolType.Modal;
}
