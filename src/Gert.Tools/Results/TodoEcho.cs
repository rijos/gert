namespace Gert.Tools.Results;

/// <summary>One echoed todo in a <see cref="TodoToolResult"/> - the text and a snake_case status string.</summary>
public sealed record TodoEcho
{
    public required string Text { get; init; }

    public required string Status { get; init; }
}
