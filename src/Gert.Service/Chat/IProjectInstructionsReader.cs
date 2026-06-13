namespace Gert.Service.Chat;

/// <summary>
/// A best-effort seam for the project's pinned system context (chat-and-tools.md
/// section tool loop, step 0). It surfaces the project's <c>instructions</c> - the
/// always-injected custom system prompt (configuration.md section 2.3), a column of the
/// <c>user.db</c> project registry - which <see cref="TurnPlanner"/> appends to
/// the system prompt when planning a turn.
/// <para>
/// It is its own narrow port (not <c>IProjectService</c>) so the planner depends
/// only on the one thing it needs, and so a host that hasn't wired a registry
/// reader yet can leave it unregistered - <see cref="TurnPlanner"/> treats a
/// missing reader as "no instructions" rather than failing the turn.
/// </para>
/// <para>
/// // TODO: extend with pinned-memory retrieval
/// (<c>documents.kind='memory' AND pinned=1</c>) once the markdown body of a
/// memory entry is reachable through a service-layer port - the
/// <c>rag.db documents</c> row exposes <c>Pinned</c> but not the note text, which
/// lives under <c>projects/{pid}/memory/</c>.
/// </para>
/// </summary>
public interface IProjectInstructionsReader
{
    /// <summary>
    /// Return the project's always-injected instructions for the caller's
    /// <c>(iss, sub)</c> and the given <paramref name="pid"/>, or <c>null</c> when
    /// the project has none. Implementations must be tolerant: a missing project
    /// or unreadable registry returns <c>null</c>, never throws into the turn.
    /// </summary>
    Task<string?> GetInstructionsAsync(
        string iss,
        string sub,
        string pid,
        CancellationToken cancellationToken = default);
}
