using System.Security.Cryptography;
using System.Text;

namespace Gert.Storage;

/// <summary>
/// The identity -> storage-key policy shared by every storage and database adapter
/// (decisions section 3, security F12): the user key derivation and the input-shape
/// guards for project ids and admin-supplied user keys. This is core security
/// policy - adapters (local FS, S3, SQLite, Postgres) consume it, never redefine it.
/// </summary>
public static class StorageKeys
{
    /// <summary>The literal landing-project id, always present (storage-and-data.md section layout).</summary>
    public const string DefaultProjectId = "default";

    /// <summary>
    /// User key - <c>sha256(iss + "\n" + sub)</c> lowercase hex (decisions section 3):
    /// fixed-length, path/key-safe, and traversal-proof for any value the IdP emits.
    /// </summary>
    public static string UserKey(string iss, string sub) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{iss}\n{sub}"))).ToLowerInvariant();

    /// <summary>
    /// Reject any <paramref name="pid"/> that is not a UUID or the literal
    /// <c>default</c> (configuration.md section 2.5). Rejecting the shape up front means
    /// <c>..</c>, separators and absolute paths never reach a path/key join.
    /// </summary>
    public static void ValidatePid(string pid)
    {
        ArgumentNullException.ThrowIfNull(pid);

        if (pid == DefaultProjectId)
        {
            return;
        }

        if (!Guid.TryParseExact(pid, "D", out _))
        {
            throw new ArgumentException(
                $"Invalid project id '{pid}'; must be a UUID (8-4-4-4-12) or the literal 'default'.",
                nameof(pid));
        }
    }

    /// <summary>
    /// Reject any admin-supplied user <paramref name="key"/> that is not exactly a
    /// lowercase sha256 hex string (security F6) - so a key can never name a path,
    /// a prefix, or anything but one user's storage root.
    /// </summary>
    public static void ValidateUserKey(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (key.Length != 64 || !key.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "Invalid user key; must be 64 lowercase hex characters (sha256).", nameof(key));
        }
    }
}
