namespace Gert.Model.Events;

/// <summary>
/// One question inside a <see cref="QuestionAskedEvent"/>: the prompt, an
/// optional short tab <see cref="Header"/> (falls back to "Question N" in the
/// UI), the closed answer choices, and whether a typed answer is also accepted.
/// </summary>
public sealed record AskedQuestion(
    string Question,
    string? Header,
    IReadOnlyList<string> Options,
    bool AllowFreeText);
