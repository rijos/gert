namespace Gert.Tools;

/// <summary>
/// One prompt put to the user (chat-and-tools.md section ask the user): the question
/// <see cref="Text"/>, an optional short <see cref="Header"/> label, a closed set of
/// <see cref="Options"/>, and whether a typed answer is also accepted. The transport-neutral
/// generalization of the chat layer's QuestionItem/AskedQuestion.
/// </summary>
public sealed record InteractionPrompt(
    string Text,
    string? Header,
    IReadOnlyList<string> Options,
    bool AllowFreeText);
