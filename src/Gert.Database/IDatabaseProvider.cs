namespace Gert.Database;

/// <summary>
/// The persistence/provisioning seam (tech-stack.md § Architecture / Engine
/// portability). Resolves a per-<c>(iss, sub, pid)</c> pair of repositories,
/// running lazy provisioning + migrations on first touch (storage-and-data.md
/// § lazy provisioning). The service layer talks only to the returned
/// repositories, never to a connection or engine type — so swapping engines is
/// one DI change.
/// </summary>
public interface IDatabaseProvider
{
    /// <summary>
    /// Validate the identity (fail-closed, before any disk access), create the
    /// user folder + <c>settings.json</c> + <c>default</c> project if absent, and
    /// verify the <c>meta.json</c> <c>(iss, sub)</c> binding (security F12).
    /// Idempotent.
    /// </summary>
    Task EnsureProvisionedAsync(
        string iss,
        string sub,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensure a project folder exists and its <c>chat.db</c>/<c>rag.db</c> are
    /// migrated to the current schema version. <paramref name="pid"/> is a
    /// validated UUID or the literal <c>default</c>. Idempotent.
    /// </summary>
    Task EnsureProjectAsync(
        string iss,
        string sub,
        string pid,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Open the chat repository for one project (open-per-use; the caller
    /// disposes it). Provisioning/migration is ensured first.
    /// </summary>
    Task<IChatRepository> OpenChatAsync(
        string iss,
        string sub,
        string pid,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Open the RAG repository for one project (open-per-use; the caller
    /// disposes it). Provisioning/migration is ensured first.
    /// </summary>
    Task<IRagRepository> OpenRagAsync(
        string iss,
        string sub,
        string pid,
        CancellationToken cancellationToken = default);
}
