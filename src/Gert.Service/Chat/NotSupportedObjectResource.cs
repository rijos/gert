using Gert.Tools;

namespace Gert.Service.Chat;

/// <summary>
/// An <see cref="IObjectResource"/> that throws on every call - the autonomous sub-agent host's
/// Objects surface. The delegable tool set ([rag, search, fetch, clock]) never touches objects,
/// so any access is a wiring bug, surfaced loudly rather than silently scoped to nothing.
/// </summary>
internal sealed class NotSupportedObjectResource : IObjectResource
{
    public Task<StoredObject?> GetAsync(ResourceScope scope, string name, CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public Task<IReadOnlyList<ObjectSummary>> ListAsync(ResourceScope scope, CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public Task<StoredObject> PutAsync(ResourceScope scope, ObjectWrite write, CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public Task<bool> DeleteAsync(ResourceScope scope, string name, CancellationToken cancellationToken = default) =>
        throw Unsupported();

    private static NotSupportedException Unsupported() =>
        new("The object store is not available to a sub-agent.");
}
