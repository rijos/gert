using System.Security.Cryptography;

namespace Gert.Web.Bundle;

/// <summary>
/// The shared, fail-closed SHA-512 pin check for the no-npm Go binaries (esbuild + tsgo):
/// hash the fetched tarball and constant-time-compare it to the pinned npm <c>dist.integrity</c>
/// digest (base64 of the raw 64-byte SHA-512). A mismatch throws, so a tampered or wrong download
/// is rejected and never used. <c>internal</c> so the manifest tests can lock the reject contract.
/// </summary>
internal static class TarballVerifier
{
    /// <summary>
    /// Throw <see cref="InvalidOperationException"/> unless <paramref name="bytes"/> hash to
    /// <paramref name="expectedBase64"/>. <paramref name="what"/> names the binary/URL for the error.
    /// </summary>
    internal static void VerifySha512(byte[] bytes, string expectedBase64, string what)
    {
        var actual = Convert.ToBase64String(SHA512.HashData(bytes));
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromBase64String(actual), Convert.FromBase64String(expectedBase64)))
        {
            throw new InvalidOperationException(
                $"{what} tarball SHA-512 mismatch.\n  expected: {expectedBase64}\n  actual:   {actual}");
        }
    }
}
