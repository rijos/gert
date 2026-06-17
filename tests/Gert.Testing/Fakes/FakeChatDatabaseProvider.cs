using Gert.Database;
using Gert.Service;

namespace Gert.Testing.Fakes;

/// <summary>
/// An <see cref="IChatDatabaseProvider"/> that hands back one fixed repository for
/// every open - pairs with <see cref="InMemoryArtifactRepository"/> so the artifact
/// tools (which open chat.db per-use) run against an in-memory store in tests.
/// </summary>
public sealed class FakeChatDatabaseProvider(IChatRepository repo) : IChatDatabaseProvider
{
    public Task<IChatRepository> OpenAsync(
        string iss, string sub, string pid, CancellationToken cancellationToken = default) =>
        Task.FromResult(repo);

    /// <summary>No-op: the in-memory repository has no file to drop (tests don't delete projects).</summary>
    public Task<bool> DeleteProjectAsync(
        string iss, string sub, string pid, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}
