using Gert.Tools.Schema;

namespace Gert.Tools.Args;

/// <summary>
/// Arguments for the sub-agent tool (<c>run_sub_agent</c>): a self-contained <see cref="Task"/> and
/// optional <see cref="Context"/> handed to a fresh nested conversation (chat-and-tools.md section
/// sub-agent). The size caps are DoS brakes the model reads in prose; <c>SubAgentArgsValidator</c>
/// enforces them fail-closed before the delegate runs.
/// </summary>
public sealed record SubAgentArgs
{
    /// <summary>Cap on the task text (a delegated brief, not a transcript).</summary>
    public const int MaxTaskChars = 8_000;

    /// <summary>Cap on the optional background material.</summary>
    public const int MaxContextChars = 32_000;

    /// <summary>The complete, self-contained task (required, non-empty).</summary>
    [ToolParameterDescription("The complete, self-contained task.")]
    public string Task { get; init; } = string.Empty;

    /// <summary>Optional background material the task needs.</summary>
    [ToolParameterDescription("Optional background material the task needs.")]
    public string? Context { get; init; }
}
