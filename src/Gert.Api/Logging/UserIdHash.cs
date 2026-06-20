using System.Security.Cryptography;
using System.Text;

namespace Gert.Api.Logging;

/// <summary>
/// Derives the log <c>uid</c> - a short prefix of the user folder key
/// <c>sha256(iss + "\n" + sub)</c> (operations.md section Logging format). It correlates a
/// user across log lines and lines logs up with the on-disk folder + admin API,
/// <b>without ever exposing the raw <c>sub</c></b>. The full key is the filesystem
/// anchor; the log only ever carries this truncated hash.
/// </summary>
public static class UserIdHash
{
    /// <summary>Hex characters of the SHA-256 key kept for the log <c>uid</c> (12 hex = 48 bits).</summary>
    public const int PrefixLength = 12;

    /// <summary>
    /// The short identity hash for (<paramref name="iss"/>, <paramref name="sub"/>): the
    /// first <see cref="PrefixLength"/> lowercase-hex chars of <c>sha256(iss + "\n" + sub)</c>.
    /// Same input shape as the folder key, so the prefix matches the folder name's prefix.
    /// </summary>
    public static string Compute(string iss, string sub)
    {
        ArgumentNullException.ThrowIfNull(iss);
        ArgumentNullException.ThrowIfNull(sub);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{iss}\n{sub}"));
        return Convert.ToHexString(hash).ToLowerInvariant()[..PrefixLength];
    }
}
