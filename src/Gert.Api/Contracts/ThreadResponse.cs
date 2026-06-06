using Gert.Model;
using Gert.Model.Chat;
using Gert.Model.Dtos;

namespace Gert.Api.Contracts;

/// <summary>
/// The wire shape of <c>GET /api/projects/{pid}/conversations/{id}</c> (rest-api.md
/// § conversations): the conversation flattened to the top level with its messages
/// (each carrying its own citations) and artifacts, so the SPA consumes it directly —
/// <c>conv.id</c>, <c>conv.title</c>, <c>conv.messages[].text</c> — with no remapping.
/// <para>
/// Note: a message's tool-call cards are <b>not</b> reconstructed here; the live cards
/// come from the SSE <c>tool_call</c>/<c>tool_result</c> stream. Reloading a thread shows
/// the text + citations (reconstructing cards from persisted calls is a separate concern).
/// </para>
/// </summary>
public sealed record ThreadResponse
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string ModelId { get; init; }

    public required ToolToggles Tools { get; init; }

    public required GenerationParams Params { get; init; }

    /// <summary>Conversation reasoning preference (null = model default). Wire: <c>thinking</c>.</summary>
    public bool? Thinking { get; init; }

    /// <summary>Interleaved-thinking preference. Wire: <c>preserve_thinking</c>.</summary>
    public bool? PreserveThinking { get; init; }

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

        var conversation = thread.Conversation;
        return new ThreadResponse
        {
            Id = conversation.Id,
            Title = conversation.Title,
            ModelId = conversation.ModelId,
            Tools = conversation.Tools,
            Params = conversation.Params,
            Thinking = conversation.Thinking,
            PreserveThinking = conversation.PreserveThinking,
            CreatedAt = conversation.CreatedAt,
            UpdatedAt = conversation.UpdatedAt,
            Archived = conversation.Archived,
            Artifacts = thread.Artifacts,
            Messages = thread.Messages
                .Select(m => ThreadMessage.From(
                    m,
                    citationsByMessage.GetValueOrDefault(m.Id) ?? []))
                .ToList(),
        };
    }
}
