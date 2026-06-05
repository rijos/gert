namespace Gert.Service.Chat;

/// <summary>
/// The <see cref="IUserContext"/> for worker scopes (chat-and-tools.md § detached
/// turns): the turn runner executes scoped tools (e.g. the rag tool) off the
/// request thread, where <c>HttpUserContext</c> has no <c>HttpContext</c>. The
/// worker seeds this from the <see cref="TurnJob"/>'s identity + entitlement
/// snapshot before resolving anything else in the scope — so "the claim is the
/// ceiling" holds off-thread exactly as captured at plan time.
/// </summary>
public sealed class DetachedUserContext : IUserContext
{
    private TurnJob? _job;

    /// <summary>Seed the scope's identity from the job. Call once, first.</summary>
    public void Seed(TurnJob job) => _job = job ?? throw new ArgumentNullException(nameof(job));

    private TurnJob Job => _job
        ?? throw new InvalidOperationException(
            "DetachedUserContext not seeded — the worker must Seed() before resolving user-scoped services.");

    /// <inheritdoc />
    public string Sub => Job.Sub;

    /// <inheritdoc />
    public string Iss => Job.Iss;

    /// <inheritdoc />
    public string Username => Job.Username;

    /// <inheritdoc />
    public bool IsAdmin => Job.IsAdmin;

    /// <inheritdoc />
    public IReadOnlySet<string> AllowedTools => Job.AllowedToolIds;

    /// <inheritdoc />
    public bool CanUseTool(string id) => AllowedTools.Contains(id);
}
