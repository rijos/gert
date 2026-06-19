namespace Gert.Model.Chat;

/// <summary>A tool call requested by the model - the name and raw JSON arguments.</summary>
public sealed record ChatModelToolCall
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string ArgumentsJson { get; init; }
}
