using Gert.Database;
using Gert.Model.Chat;
using Gert.Storage;
using Gert.Tools;
using Gert.Tools.Resources;

namespace Gert.Service.Chat;

/// <summary>
/// The chat-scoped <see cref="IObjectResource"/> over a conversation's <c>chat_objects</c>
/// rows (chat-and-tools.md section objects resource): create-or-overwrite by name, versioned
/// on overwrite. Pre-bound to one conversation at construction, so the artifact tools
/// (make/edit/read) reach only this conversation's objects - they supply neither identity nor
/// a storage key, only a <see cref="ResourceScope"/> + name. Only <see cref="ResourceScope.Chat"/>
/// is supported; project-scoped objects are wired in a later phase.
/// </summary>
internal sealed class ChatObjectResource : IObjectResource
{
    private readonly IChatRepository _repo;
    private readonly string _conversationId;
    private readonly TimeProvider _clock;

    public ChatObjectResource(IChatRepository repo, string conversationId, TimeProvider clock)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        ArgumentNullException.ThrowIfNull(conversationId);
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));

        // Defence in depth: the id rides the pre-scoped host, but validate its shape so a
        // malformed conversation id never reaches a query (StorageKeys.ValidateConversationId).
        StorageKeys.ValidateConversationId(conversationId);
        _conversationId = conversationId;
    }

    public async Task<StoredObject?> GetAsync(
        ResourceScope scope, string name, CancellationToken cancellationToken = default)
    {
        RequireChat(scope);
        var artifact = await _repo.GetArtifactByNameAsync(_conversationId, name, cancellationToken)
            .ConfigureAwait(false);
        return artifact is null ? null : ToStored(artifact);
    }

    public async Task<IReadOnlyList<ObjectSummary>> ListAsync(
        ResourceScope scope, CancellationToken cancellationToken = default)
    {
        RequireChat(scope);
        var artifacts = await _repo.ListArtifactsAsync(_conversationId, cancellationToken).ConfigureAwait(false);
        return artifacts.Select(a => new ObjectSummary
        {
            Id = a.Id,
            Name = a.Name,
            Version = a.Version,
            Kind = ArtifactKinds.ToToken(a.Kind),
            CreatedAt = a.CreatedAt,
            UpdatedAt = a.UpdatedAt,
        }).ToList();
    }

    public async Task<StoredObject> PutAsync(
        ResourceScope scope, ObjectWrite write, CancellationToken cancellationToken = default)
    {
        RequireChat(scope);
        ArgumentNullException.ThrowIfNull(write);

        var existing = await _repo.GetArtifactByNameAsync(_conversationId, write.Name, cancellationToken)
            .ConfigureAwait(false);

        Artifact written;
        if (existing is not null)
        {
            written = existing with
            {
                Kind = ArtifactKinds.FromToken(write.Kind),
                Content = write.Content,
                Version = existing.Version + 1,
                UpdatedAt = _clock.GetUtcNow(),
            };
            await _repo.UpdateArtifactAsync(written, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var now = _clock.GetUtcNow();
            written = new Artifact
            {
                Id = Guid.NewGuid().ToString("D"),
                ConversationId = _conversationId,
                MessageId = null,
                Kind = ArtifactKinds.FromToken(write.Kind),
                Name = write.Name,
                Language = null,
                Content = write.Content,
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now,
            };
            await _repo.InsertArtifactAsync(written, cancellationToken).ConfigureAwait(false);
        }

        return ToStored(written);
    }

    public Task<bool> DeleteAsync(
        ResourceScope scope, string name, CancellationToken cancellationToken = default)
    {
        RequireChat(scope);
        return _repo.DeleteArtifactByNameAsync(_conversationId, name, cancellationToken);
    }

    private static void RequireChat(ResourceScope scope)
    {
        if (scope != ResourceScope.Chat)
        {
            throw new NotSupportedException("project-scoped objects are wired in a later phase");
        }
    }

    private static StoredObject ToStored(Artifact a) => new()
    {
        Id = a.Id,
        Name = a.Name,
        Content = a.Content,
        Version = a.Version,
        Kind = ArtifactKinds.ToToken(a.Kind),
        CreatedAt = a.CreatedAt,
        UpdatedAt = a.UpdatedAt,
    };
}
