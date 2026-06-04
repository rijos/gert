namespace Gert.Service.Chat;

/// <summary>
/// A best-effort seam for the project's pinned system context (chat-and-tools.md
/// § tool loop, step 0). Today it surfaces the project's <c>meta.json</c>
/// <c>instructions</c> — the always-injected custom system prompt
/// (configuration.md § 2.3) — which <see cref="ChatService"/> prepends to the
/// system prompt at the start of a turn.
/// <para>
/// It is its own narrow port (not <c>IProjectService</c>) so the orchestrator
/// depends only on the one thing it needs, and so a host that hasn't wired a
/// data-root reader yet can leave it unregistered — <see cref="ChatService"/>
/// treats a missing reader as "no instructions" rather than failing the turn.
/// The adapter (<c>Gert.Database.Sqlite</c>, U10) reads <c>meta.json</c>.
/// </para>
/// <para>
/// // TODO U7b/U10: extend with pinned-memory retrieval
/// (<c>documents.kind='memory' AND pinned=1</c>) once the markdown body of a
/// memory entry is reachable through a service-layer port — the
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
    /// or unreadable meta returns <c>null</c>, never throws into the turn.
    /// </summary>
    Task<string?> GetInstructionsAsync(
        string iss,
        string sub,
        string pid,
        CancellationToken cancellationToken = default);
}
