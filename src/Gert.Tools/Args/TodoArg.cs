using Gert.Tools.Schema;

namespace Gert.Tools.Args;

/// <summary>
/// One entry of a <see cref="TodoArgs"/> list: the step <see cref="Text"/> and a
/// raw <see cref="Status"/> string (<c>pending</c> / <c>active</c> / <c>done</c>).
/// The status stays a string at the wire so the validator rejects an unknown one
/// with a model-correctable error before the tool maps it onto <c>TodoStatus</c>.
/// </summary>
public sealed record TodoArg
{
    /// <summary>The step, imperative and short (required, non-empty).</summary>
    [ToolParameterDescription("The step, imperative and short.")]
    public string Text { get; init; } = string.Empty;

    /// <summary>The step status: pending, active, or done (required).</summary>
    [ToolParameterEnum("pending", "active", "done")]
    public string Status { get; init; } = string.Empty;
}
