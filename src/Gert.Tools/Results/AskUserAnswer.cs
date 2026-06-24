namespace Gert.Tools.Results;

/// <summary>
/// One answered prompt of an <see cref="AskUserResult"/>: the <see cref="Question"/> text paired
/// with the user's <see cref="Answer"/>, so the model has the context for which reply is which.
/// </summary>
public sealed record AskUserAnswer
{
    public required string Question { get; init; }

    public required string Answer { get; init; }
}
