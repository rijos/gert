namespace Gert.Service.Admin;

/// <summary>
/// The operator-facing prompt snapshot: the built-in system prompt plus every
/// registered tool spec.
/// </summary>
public sealed record SystemPromptSnapshot
{
    /// <summary>
    /// The built-in system prompt every turn carries (step 0). Per-project
    /// pinned instructions append to this at plan time and are per-user data -
    /// they are NOT included here (admin grants no cross-user data read,
    /// auth.md section matrix).
    /// </summary>
    public required string SystemPrompt { get; init; }

    public required IReadOnlyList<ToolSpecView> Tools { get; init; }
}
