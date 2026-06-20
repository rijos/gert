namespace Gert.Agent;

/// <summary>
/// One question in a <see cref="QuestionPayload"/>: the prompt, an optional
/// short tab <see cref="Header"/>, a closed option set, and whether free text is
/// also accepted.
/// </summary>
public sealed record QuestionItem(
    string Question,
    string? Header,
    IReadOnlyList<string> Options,
    bool AllowFreeText);
