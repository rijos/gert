using Gert.Database;
using Gert.Model.Chat;

namespace Gert.Testing.Fakes;

/// <summary>
/// A tiny <see cref="IChatRepository"/> that backs only the artifact operations
/// the canvas tools (make/edit/read) touch, with an in-memory store. Everything
/// else throws - this is a tool-level fake, not a full chat.db stand-in. Used by
/// the live vLLM integration tests to execute the artifact tools end-to-end
/// without a real database.
/// </summary>
public sealed class InMemoryArtifactRepository : IChatRepository
{
    private readonly List<Artifact> _artifacts = [];

    public IReadOnlyList<Artifact> Artifacts => _artifacts;

    public Task<IReadOnlyList<Artifact>> ListArtifactsAsync(
        string conversationId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Artifact>>(
            _artifacts.Where(a => a.ConversationId == conversationId).ToList());

    public Task<Artifact?> GetArtifactAsync(string artifactId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_artifacts.FirstOrDefault(a => a.Id == artifactId));

    public Task<Artifact?> GetArtifactByNameAsync(
        string conversationId, string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(_artifacts.LastOrDefault(a => a.ConversationId == conversationId && a.Name == name));

    public Task InsertArtifactAsync(Artifact artifact, CancellationToken cancellationToken = default)
    {
        _artifacts.Add(artifact);
        return Task.CompletedTask;
    }

    public Task UpdateArtifactAsync(Artifact artifact, CancellationToken cancellationToken = default)
    {
        var i = _artifacts.FindIndex(a => a.Id == artifact.Id);
        if (i >= 0)
        {
            _artifacts[i] = artifact;
        }

        return Task.CompletedTask;
    }

    public Task<bool> DeleteArtifactByNameAsync(
        string conversationId, string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(_artifacts.RemoveAll(a => a.ConversationId == conversationId && a.Name == name) > 0);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static T Unsupported<T>() =>
        throw new NotSupportedException("InMemoryArtifactRepository only backs artifact operations.");

    public Task<IReadOnlyList<Conversation>> ListConversationsAsync(CancellationToken cancellationToken = default) => Unsupported<Task<IReadOnlyList<Conversation>>>();

    public Task<Conversation?> GetConversationAsync(string conversationId, CancellationToken cancellationToken = default) => Unsupported<Task<Conversation?>>();

    public Task<ConversationThread?> GetThreadAsync(string conversationId, CancellationToken cancellationToken = default) => Unsupported<Task<ConversationThread?>>();

    public Task InsertConversationAsync(Conversation conversation, CancellationToken cancellationToken = default) => Unsupported<Task>();

    public Task UpdateConversationAsync(Conversation conversation, CancellationToken cancellationToken = default) => Unsupported<Task>();

    public Task<bool> DeleteConversationAsync(string conversationId, CancellationToken cancellationToken = default) => Unsupported<Task<bool>>();

    public Task<IReadOnlyList<Message>> ListMessagesAsync(string conversationId, CancellationToken cancellationToken = default) => Unsupported<Task<IReadOnlyList<Message>>>();

    public Task InsertMessageAsync(Message message, CancellationToken cancellationToken = default) => Unsupported<Task>();

    public Task<bool> TryInsertTurnMessagesAsync(Message userMessage, Message assistantMessage, CancellationToken cancellationToken = default) => Unsupported<Task<bool>>();

    public Task<bool> TryExpireStreamingMessageAsync(string messageId, CancellationToken cancellationToken = default) => Unsupported<Task<bool>>();

    public Task UpdateMessageStreamAsync(string messageId, string content, MessageStatus status, int? tokenCount, CancellationToken cancellationToken = default) => Unsupported<Task>();

    public Task FinalizeMessageAsync(string messageId, string content, MessageStatus status, int? tokenCount, string? reasoning, long? durationMs, int? contextTokens, CancellationToken cancellationToken = default) => Unsupported<Task>();

    public Task<long> AllocateSeqAsync(string conversationId, CancellationToken cancellationToken = default) => Unsupported<Task<long>>();

    public Task AppendTurnEventAsync(TurnEventRecord turnEvent, CancellationToken cancellationToken = default) => Unsupported<Task>();

    public Task<IReadOnlyList<TurnEventRecord>> ReadTurnEventsAsync(string conversationId, long afterSeq, int limit, CancellationToken cancellationToken = default) => Unsupported<Task<IReadOnlyList<TurnEventRecord>>>();

    public Task InsertToolCallAsync(ToolCall toolCall, CancellationToken cancellationToken = default) => Unsupported<Task>();

    public Task<ToolCall?> GetLatestToolCallAsync(string conversationId, string kind, CancellationToken cancellationToken = default) => Unsupported<Task<ToolCall?>>();

    public Task InsertCitationsAsync(IReadOnlyList<Citation> citations, CancellationToken cancellationToken = default) => Unsupported<Task>();
}
