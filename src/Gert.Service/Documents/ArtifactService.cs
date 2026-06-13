using Gert.Database;
using Gert.Model.Chat;

namespace Gert.Service.Documents;

/// <summary>
/// Reads chat artifacts (the canvas tabs) from the project's <c>chat.db</c>
/// (rest-api.md section artifacts). Read-only; artifacts are produced by the chat loop
/// Scoped to the current user via <see cref="IUserContext"/>.
/// </summary>
public sealed class ArtifactService : IArtifactService
{
    private readonly IChatDatabaseProvider _databases;
    private readonly IUserContext _user;

    public ArtifactService(IChatDatabaseProvider databases, IUserContext user)
    {
        _databases = databases ?? throw new ArgumentNullException(nameof(databases));
        _user = user ?? throw new ArgumentNullException(nameof(user));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Artifact>> ListAsync(
        string pid,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        await using var repo = await _databases
            .OpenAsync(_user.Iss, _user.Sub, pid, cancellationToken)
            .ConfigureAwait(false);
        return await repo.ListArtifactsAsync(conversationId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Artifact?> GetAsync(
        string pid,
        string artifactId,
        CancellationToken cancellationToken = default)
    {
        await using var repo = await _databases
            .OpenAsync(_user.Iss, _user.Sub, pid, cancellationToken)
            .ConfigureAwait(false);
        return await repo.GetArtifactAsync(artifactId, cancellationToken).ConfigureAwait(false);
    }
}
