namespace Gert.Database.Sqlite;

/// <summary>
/// Thrown when an existing user folder's <c>meta.json</c> identity binding does
/// not equal the token's <c>(iss, sub)</c> — a recreated/reassigned identity
/// resolving onto another's folder is refused, never served (security F12).
/// </summary>
public sealed class IdentityBindingException : Exception
{
    /// <summary>Create with a human-readable reason.</summary>
    public IdentityBindingException(string message)
        : base(message)
    {
    }

    /// <summary>Create with a reason and inner cause.</summary>
    public IdentityBindingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
