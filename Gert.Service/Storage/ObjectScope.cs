namespace Gert.Service.Storage;

/// <summary>
/// The project-scoped root for an <see cref="IObjectStore"/> operation — the
/// validated <c>(iss, sub, pid)</c> that resolves a single project's <c>files/</c>
/// directory (storage-and-data.md § layout). Mirrors how the database seam
/// (<see cref="Database.IDatabaseProvider"/>) threads identity: <c>(iss, sub)</c>
/// come only from the validated token and <c>pid</c> is a validated UUID or the
/// literal <c>default</c>, so a scope can never select another user's or project's
/// blobs. Keys are then resolved <b>under</b> this scope and guarded against
/// escaping it.
/// </summary>
/// <param name="Iss">Token issuer — combined with <paramref name="Sub"/> to derive the user folder key.</param>
/// <param name="Sub">Stable IdP subject id — the user folder anchor.</param>
/// <param name="Pid">Project id — a UUID or the literal <c>default</c>.</param>
public readonly record struct ObjectScope(string Iss, string Sub, string Pid);
