using System.Collections.Concurrent;

namespace Gert.Authentication;

/// <summary>
/// Fast-revocation denylist keyed on the token <c>sub</c> (decisions §4). Pocket ID's
/// ~1-hour access-token lifetime is the routine off-boarding window; this is the lever
/// for <em>immediate</em> cut-off. Checked in the JwtBearer <c>OnTokenValidated</c>
/// event — a denied <c>sub</c> fails authentication even with an otherwise-valid token.
/// Kept deliberately small: this is the one piece of shared, mutable auth state the
/// design accepts.
/// </summary>
public interface ISubDenylist
{
    /// <summary>True when the given <c>sub</c> has been revoked and must be rejected.</summary>
    bool IsDenied(string sub);
}

/// <summary>
/// Simple in-process <see cref="ISubDenylist"/>. Adequate for the single-host, ~20-user
/// deployment; a denied <c>sub</c> takes effect immediately for every request the host
/// validates. Thread-safe. A multi-host deployment would back this with a shared store,
/// but that is out of scope here.
/// </summary>
public sealed class InMemorySubDenylist : ISubDenylist
{
    private readonly ConcurrentDictionary<string, byte> _denied = new(StringComparer.Ordinal);

    /// <summary>Revoke a <c>sub</c> — all of its tokens are rejected from now on.</summary>
    public void Deny(string sub)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sub);
        _denied[sub] = 0;
    }

    /// <summary>Re-admit a previously denied <c>sub</c>.</summary>
    public void Allow(string sub)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sub);
        _denied.TryRemove(sub, out _);
    }

    /// <inheritdoc />
    public bool IsDenied(string sub) =>
        !string.IsNullOrEmpty(sub) && _denied.ContainsKey(sub);
}
