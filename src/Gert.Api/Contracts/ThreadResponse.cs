using Gert.Model;
using Gert.Model.Chat;
using Gert.Model.Dtos;

namespace Gert.Api.Contracts;

/// <summary>
/// The wire shape of <c>GET /api/projects/{pid}/conversations/{id}</c> (rest-api.md
/// section conversations): the conversation flattened to the top level with its messages
/// (each carrying its own citations) and artifacts, so the SPA consumes it directly with no
/// remapping. Each message also carries its tool-call cards (<see cref="ThreadToolCall"/>),
/// reconstructed from the persisted <c>tool_calls</c> rows + their citations, so a reload
/// reproduces the same cards the live <c>tool_call</c>/<c>tool_result</c> stream drew.
/// </summary>
public sealed record ThreadResponse
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string ModelId { get; init; }

    public required ToolToggles Tools { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public bool Archived { get; init; }

    public IReadOnlyList<ThreadMessage> Messages { get; init; } = [];

    public IReadOnlyList<Artifact> Artifacts { get; init; } = [];

    /// <summary>Flatten a <see cref="ConversationThread"/>, binding citations to their message.</summary>
    public static ThreadResponse From(ConversationThread thread)
    {
        ArgumentNullException.ThrowIfNull(thread);

        var citationsByMessage = thread.Citations
            .GroupBy(c => c.MessageId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Citation>)[.. g]);

        // Hits ride the Message -> ToolCall -> Citations provenance tree back up:
        // each card's rows come from the citations its call produced.
        var citationsByCall = thread.Citations
            .Where(c => c.ToolCallId is not null)
            .GroupBy(c => c.ToolCallId!)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Citation>)[.. g.OrderBy(c => c.Ordinal)]);

        var toolsByMessage = thread.ToolCalls
            .GroupBy(t => t.MessageId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<ThreadToolCall>)g
                    .OrderBy(t => t.CreatedAt)
                    .Select(t => ThreadToolCall.From(t, citationsByCall.GetValueOrDefault(t.Id) ?? []))
                    .ToList());

        var conversation = thread.Conversation;
        return new ThreadResponse
        {
            Id = conversation.Id,
            Title = conversation.Title,
            ModelId = conversation.ModelId,
            Tools = conversation.Tools,
            CreatedAt = conversation.CreatedAt,
            UpdatedAt = conversation.UpdatedAt,
            Archived = conversation.Archived,
            Artifacts = thread.Artifacts,
            Messages = thread.Messages
                .Select(m => ThreadMessage.From(
                    m,
                    citationsByMessage.GetValueOrDefault(m.Id) ?? [],
                    toolsByMessage.GetValueOrDefault(m.Id) ?? []))
                .ToList(),
        };
    }
}
