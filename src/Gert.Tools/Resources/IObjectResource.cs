namespace Gert.Tools.Resources;

/// <summary>
/// A metadata-aware store of named objects, pre-scoped by the host to a project or the active
/// conversation (chat-and-tools.md section objects resource). Create-or-overwrite by name bumps
/// the version; listing returns metadata only. Replaces the old raw-blob artifact paths -
/// the tool sees neither a storage key nor an identity, only a <see cref="ResourceScope"/> + name.
/// </summary>
public interface IObjectResource
{
    /// <summary>The object by name, or null if absent.</summary>
    Task<StoredObject?> GetAsync(ResourceScope scope, string name, CancellationToken cancellationToken = default);

    /// <summary>Metadata for every object in the scope (no content).</summary>
    Task<IReadOnlyList<ObjectSummary>> ListAsync(ResourceScope scope, CancellationToken cancellationToken = default);

    /// <summary>Create or overwrite by name, bumping <see cref="StoredObject.Version"/>; returns the stored row.</summary>
    Task<StoredObject> PutAsync(ResourceScope scope, ObjectWrite write, CancellationToken cancellationToken = default);

    /// <summary>Delete by name; false if it did not exist.</summary>
    Task<bool> DeleteAsync(ResourceScope scope, string name, CancellationToken cancellationToken = default);
}
