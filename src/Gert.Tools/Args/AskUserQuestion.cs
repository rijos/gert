using Gert.Tools.Schema;

namespace Gert.Tools.Args;

/// <summary>
/// One entry of an <see cref="AskUserArgs"/> batch: the <see cref="Question"/> text, an optional
/// short tab <see cref="Header"/>, an optional closed set of <see cref="Options"/>, and whether a
/// typed answer is also accepted. The caps are the schema-prose bounds the model reads;
/// <c>AskUserQuestionValidator</c> enforces them fail-closed before the tool builds the prompt.
/// </summary>
public sealed record AskUserQuestion
{
    /// <summary>Cap on the question text - a prompt, not an essay.</summary>
    public const int MaxQuestionChars = 2_000;

    /// <summary>Cap on a question's short tab label.</summary>
    public const int MaxHeaderChars = 60;

    /// <summary>Max closed answer choices per question (rendered as buttons).</summary>
    public const int MaxOptions = 8;

    /// <summary>Cap on one option's text.</summary>
    public const int MaxOptionChars = 200;

    /// <summary>The question to show the user (required, non-empty).</summary>
    [ToolParameterDescription("The question to show the user.")]
    public string Question { get; init; } = string.Empty;

    /// <summary>Optional short tab label; defaults to "Question N" when unset.</summary>
    [ToolParameterDescription("Optional short tab label (defaults to 'Question N').")]
    public string? Header { get; init; }

    /// <summary>Optional closed set of answer choices (max <see cref="MaxOptions"/>), rendered as buttons.</summary>
    [ToolParameterDescription("Optional closed set of answer choices (max 8), rendered as buttons.")]
    public IReadOnlyList<string>? Options { get; init; }

    /// <summary>Allow a typed answer too; null defaults to true when no options are given, false otherwise.</summary>
    [ToolParameterDescription("Allow a typed answer in addition to options. Default true when no options are given, false otherwise.")]
    public bool? AllowFreeText { get; init; }
}
