namespace Gert.Service.Chat;

/// <summary>
/// The default <see cref="IProjectInstructionsReader"/> - always "no instructions".
/// Registered with <c>TryAdd</c> so the service layer is self-contained when a host
/// has not yet wired a reader over the <c>user.db</c> project registry. A host that
/// can read the registry overrides this registration, and <see cref="TurnPlanner"/>
/// then appends the real instructions to the system prompt (step 0).
/// </summary>
public sealed class NullProjectInstructionsReader : IProjectInstructionsReader
{
    /// <inheritdoc />
    public Task<string?> GetInstructionsAsync(
        string iss,
        string sub,
        string pid,
        CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
}
