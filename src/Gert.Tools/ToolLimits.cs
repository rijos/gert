namespace Gert.Tools;

/// <summary>
/// The per-invocation budget a tool honours: the wall-clock <see cref="Deadline"/> (a modal tool
/// budgets its wait against it) and an optional <see cref="TokenBudget"/> for nested work.
/// </summary>
public sealed record ToolLimits(DateTimeOffset? Deadline, int? TokenBudget);
