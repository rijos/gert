using Gert.Tools.Schema;

namespace Gert.Tools.Args;

/// <summary>
/// Arguments for the ask-user tool (<c>ask_user</c>): one to four <see cref="AskUserQuestion"/>s
/// asked together and rendered as tabs (chat-and-tools.md section Ask the user). The count cap is
/// the schema-prose bound the model reads; <c>AskUserArgsValidator</c> enforces it fail-closed.
/// </summary>
public sealed record AskUserArgs
{
    /// <summary>Min questions in one call - a meaningful ask names at least one.</summary>
    public const int MinQuestions = 1;

    /// <summary>Max questions asked together (rendered as tabs).</summary>
    public const int MaxQuestions = 4;

    /// <summary>The questions to ask together (required, 1..<see cref="MaxQuestions"/>).</summary>
    [ToolParameterDescription("One to four questions to ask together, shown as tabs.")]
    public IReadOnlyList<AskUserQuestion> Questions { get; init; } = [];
}
