using FluentAssertions;
using Gert.Web.Bundle;
using Xunit;

namespace Gert.Web.Bundle.Tests;

/// <summary>
/// The pinned esbuild manifest is the no-npm supply-chain control (ui-components.md section 6):
/// every supported RID must carry a non-empty tarball pin so the publish download is reproducible
/// and SHA-512-verified. These run without network - the actual fetch is exercised on publish.
/// </summary>
public sealed class EsbuildManifestTests
{
    [Fact]
    public void Pins_a_concrete_version()
    {
        EsbuildManifest.Version.Should().MatchRegex(@"^\d+\.\d+\.\d+$");
    }

    [Theory]
    [InlineData("linux-x64", "linux-x64", false)]
    [InlineData("linux-arm64", "linux-arm64", false)]
    [InlineData("osx-x64", "darwin-x64", false)]
    [InlineData("osx-arm64", "darwin-arm64", false)]
    [InlineData("win-x64", "win32-x64", true)]
    public void Maps_each_rid_to_its_npm_platform(string rid, string npmKey, bool isWindows)
    {
        var p = EsbuildManifest.Platforms[rid];

        p.NpmKey.Should().Be(npmKey);
        p.IsWindows.Should().Be(isWindows);
        p.EntryPath.Should().Be(isWindows ? "package/esbuild.exe" : "package/bin/esbuild");
        p.TarballUrl.Should().Be(
            $"https://registry.npmjs.org/@esbuild/{npmKey}/-/{npmKey}-{EsbuildManifest.Version}.tgz");
    }

    [Fact]
    public void Every_platform_carries_a_decodable_sha512_pin()
    {
        foreach (var (rid, p) in EsbuildManifest.Platforms)
        {
            p.Sha512.Should().NotBeNullOrWhiteSpace($"rid {rid} needs a pin");
            // npm integrity is base64 of the raw 64-byte SHA-512 digest.
            Convert.FromBase64String(p.Sha512).Should().HaveCount(64, $"rid {rid}");
        }
    }

    [Fact]
    public void Current_resolves_to_a_pinned_platform_on_this_host()
    {
        // The test host is one of the supported RIDs; resolution must not throw.
        var current = EsbuildManifest.Current();
        EsbuildManifest.Platforms.Values.Should().Contain(current);
    }
}
