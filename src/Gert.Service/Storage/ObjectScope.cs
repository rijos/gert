namespace Gert.Service.Storage;

/// <summary>
/// The addressed root for an <see cref="IObjectStore"/> operation - either a
/// <b>user root</b> (config sidecars like <c>meta.json</c> / <c>settings.json</c>)
/// or one <b>project root</b> under it (<c>meta.json</c>, <c>files/...</c>,
/// <c>memory/...</c>). Mirrors how the database seam threads identity:
/// <c>(iss, sub)</c> come only from the validated token (hashed to the opaque
/// <see cref="UserKey"/> here, never stored raw), and <c>pid</c> is validated to a
/// UUID or the literal <c>default</c> - so a scope can never select another user's
/// or project's objects. Keys are then resolved <b>under</b> this scope and
/// guarded against escaping it.
/// </summary>
public readonly record struct ObjectScope
{
    private ObjectScope(string userKey, string? pid)
    {
        UserKey = userKey;
        Pid = pid;
    }

    /// <summary>The opaque user key - <c>sha256(iss + "\n" + sub)</c> (decisions section 3).</summary>
    public string UserKey { get; }

    /// <summary>The project id, or <see langword="null"/> for a user-root scope.</summary>
    public string? Pid { get; }

    /// <summary>True when this scope addresses one project under the user root.</summary>
    public bool IsProject => Pid is not null;

    /// <summary>The user-root scope for the validated token identity.</summary>
    public static ObjectScope User(string iss, string sub) =>
        new(StorageKeys.UserKey(iss, sub), null);

    /// <summary>One project's scope for the validated token identity (+ validated pid).</summary>
    public static ObjectScope Project(string iss, string sub, string pid)
    {
        StorageKeys.ValidatePid(pid);
        return new(StorageKeys.UserKey(iss, sub), pid);
    }

    /// <summary>
    /// The user-root scope for an admin-supplied folder <paramref name="key"/>
    /// (shape-validated here again - security F6 defence-in-depth).
    /// </summary>
    public static ObjectScope FromUserKey(string key)
    {
        StorageKeys.ValidateUserKey(key);
        return new(key, null);
    }

    /// <summary>This user's scope narrowed to one validated project.</summary>
    public ObjectScope ForProject(string pid)
    {
        StorageKeys.ValidatePid(pid);
        return new(UserKey, pid);
    }
}
