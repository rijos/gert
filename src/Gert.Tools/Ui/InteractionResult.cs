namespace Gert.Tools.Ui;

/// <summary>
/// The outcome of an <see cref="IToolUi.AskAsync"/> (chat-and-tools.md section ask the user):
/// <see cref="Answered"/>+<see cref="Answers"/> on success (one answer per prompt, in order);
/// Answered=false with empty Answers on timeout (the graceful "no response" path); or
/// <see cref="Error"/> set (Answered=false) for a model-correctable rejection (e.g. a question
/// already pending).
/// </summary>
public sealed record InteractionResult
{
    public required bool Answered { get; init; }

    /// <summary>One answer per prompt, in prompt order. Empty unless <see cref="Answered"/>.</summary>
    public IReadOnlyList<string> Answers { get; init; } = [];

    /// <summary>A model-correctable rejection message; null on success or timeout.</summary>
    public string? Error { get; init; }
}
