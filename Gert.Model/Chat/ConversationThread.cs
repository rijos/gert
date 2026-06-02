namespace Gert.Model.Chat;

/// <summary>
/// A fully-loaded conversation thread — the <c>GET
/// /api/projects/{pid}/conversations/{id}</c> shape (rest-api.md
/// § conversations): the conversation plus its messages, tool calls, citations,
/// and artifacts, so reloading reproduces the same cards/citations/artifacts.
/// </summary>
public sealed record ConversationThread
{
    public required Conversation Conversation { get; init; }

    public IReadOnlyList<Message> Messages { get; init; } = [];

    public IReadOnlyList<ToolCall> ToolCalls { get; init; } = [];

    public IReadOnlyList<Citation> Citations { get; init; } = [];

    public IReadOnlyList<Artifact> Artifacts { get; init; } = [];
}
