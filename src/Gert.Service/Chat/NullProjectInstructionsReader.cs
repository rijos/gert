namespace Gert.Service.Chat;

/// <summary>
/// The default <see cref="IProjectInstructionsReader"/> — always "no instructions".
/// Registered with <c>TryAdd</c> so the service layer is self-contained when a host
/// has not yet wired a data-root reader (project meta lives in the filesystem, read
/// by the database adapter in U10). A host that can read <c>meta.json</c> overrides
/// this registration, and <see cref="ChatService"/> then prepends the real
/// instructions (step 0).
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
