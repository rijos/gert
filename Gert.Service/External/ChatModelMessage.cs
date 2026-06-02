namespace Gert.Service.External;

/// <summary>One message in the model conversation sent upstream.</summary>
public sealed record ChatModelMessage
{
    /// <summary>OpenAI-style role: <c>system</c> | <c>user</c> | <c>assistant</c> | <c>tool</c>.</summary>
    public required string Role { get; init; }

    public required string Content { get; init; }

    /// <summary>For tool-result messages: the id of the tool call this responds to.</summary>
    public string? ToolCallId { get; init; }
}
