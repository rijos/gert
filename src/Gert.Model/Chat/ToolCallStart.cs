namespace Gert.Model.Chat;

/// <summary>
/// Announced the moment a streamed tool call's <c>name</c> first appears - before
/// its arguments finish - so the orchestrator can surface the model's intent live
/// (a Running tool card) instead of waiting for the whole call to assemble. The
/// complete call still arrives later as a <see cref="ChatModelChunk.ToolCall"/>
/// with the SAME <see cref="Id"/>.
/// </summary>
public sealed record ToolCallStart
{
    public required string Id { get; init; }

    public required string Name { get; init; }
}
