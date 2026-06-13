namespace Gert.Service.Provisioning;

/// <summary>
/// Seeds the descriptive, product-level state a user needs before their first read
/// can be meaningful - the <c>user.db</c> username row (so the admin scan can map a
/// folder key to a name) and the landing <c>default</c> project (so a brand-new
/// account lists at least one project). Identity comes from <see cref="IUserContext"/>.
///
/// <para>
/// This is the <i>only</i> eager provisioning left: the chat/rag/user databases all
/// self-provision on open. It runs once at the request boundary (the API's
/// provisioning middleware), not sprinkled before every database call.
/// </para>
/// </summary>
public interface IUserProvisioner
{
    /// <summary>
    /// Ensure the current user's <c>user.db</c> carries an up-to-date username and a
    /// <c>default</c> project row. Idempotent and cheap (a couple of indexed reads on
    /// the steady-state path); safe to call on every request.
    /// </summary>
    Task EnsureCurrentUserAsync(CancellationToken cancellationToken = default);
}
