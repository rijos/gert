namespace Gert.Database;

/// <summary>
/// Thrown by a database adapter's fail-closed provisioning gate when an identity
/// is rejected <b>before any folder is created</b> (security F12 / decisions §3):
/// an issuer that is not the configured authority, or a missing/malformed
/// <c>sub</c>. Database-layer vocabulary — shared by every <c>Gert.Database.*</c>
/// adapter (SQLite today, e.g. Postgres tomorrow), nothing else throws it.
/// </summary>
public sealed class UnauthorizedDatabaseIdentityException : Exception
{
    /// <summary>Create with a human-readable reason.</summary>
    public UnauthorizedDatabaseIdentityException(string message)
        : base(message)
    {
    }

    /// <summary>Create with a reason and inner cause.</summary>
    public UnauthorizedDatabaseIdentityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
