using System.Security.Cryptography;
using FluentAssertions;
using Gert.Web.Bundle;
using Xunit;

namespace Gert.Web.Bundle.Tests;

/// <summary>
/// The SHA-512 pin check is the no-npm supply-chain control shared by the esbuild + tsgo
/// provisioners (ui-components.md section 6). These lock the
/// fail-closed reject contract - the FixedTimeEquals path is otherwise only exercised on a live
/// download - so a future refactor can't silently turn verification off.
/// </summary>
public sealed class TarballVerifierTests
{
    private static string Sha512B64(byte[] bytes) => Convert.ToBase64String(SHA512.HashData(bytes));

    [Fact]
    public void Accepts_bytes_matching_their_pinned_digest()
    {
        var bytes = "the pinned tarball bytes"u8.ToArray();

        var act = () => TarballVerifier.VerifySha512(bytes, Sha512B64(bytes), "test");

        act.Should().NotThrow();
    }

    [Fact]
    public void Rejects_a_one_bit_flipped_digest()
    {
        var bytes = "the pinned tarball bytes"u8.ToArray();
        var digest = Convert.FromBase64String(Sha512B64(bytes));
        digest[0] ^= 0x01; // flip one bit of the expected pin
        var tampered = Convert.ToBase64String(digest);

        var act = () => TarballVerifier.VerifySha512(bytes, tampered, "test");

        act.Should().Throw<InvalidOperationException>().WithMessage("*SHA-512 mismatch*");
    }

    [Fact]
    public void Rejects_tampered_bytes_against_a_good_pin()
    {
        var original = "the pinned tarball bytes"u8.ToArray();
        var pin = Sha512B64(original);
        var tampered = "the pinned tarball byteX"u8.ToArray(); // one byte changed in the download

        var act = () => TarballVerifier.VerifySha512(tampered, pin, "test");

        act.Should().Throw<InvalidOperationException>().WithMessage("*SHA-512 mismatch*");
    }
}
