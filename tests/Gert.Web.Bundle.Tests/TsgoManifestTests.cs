using FluentAssertions;
using Gert.Web.Bundle;
using Xunit;

namespace Gert.Web.Bundle.Tests;

/// <summary>
/// The pinned tsgo manifest is the no-npm supply-chain control for the type-check gate
/// (ui-components.md section 6, "Bumping tsgo"): every supported RID must carry a decodable
/// tarball pin so the fetch is reproducible and SHA-512-verified. These run without network - the
/// actual fetch is exercised by `make typecheck`.
/// </summary>
public sealed class TsgoManifestTests
{
    [Fact]
    public void Pins_a_concrete_dev_version()
    {
        // tsgo is a daily-dev preview: 7.0.0-dev.YYYYMMDD.N, pinned EXACTLY for reproducibility.
        TsgoManifest.Version.Should().MatchRegex(@"^\d+\.\d+\.\d+-dev\.\d{8}\.\d+$");
    }

    [Theory]
    [InlineData("linux-x64", "native-preview-linux-x64", false)]
    [InlineData("linux-arm64", "native-preview-linux-arm64", false)]
    [InlineData("osx-x64", "native-preview-darwin-x64", false)]
    [InlineData("osx-arm64", "native-preview-darwin-arm64", false)]
    [InlineData("win-x64", "native-preview-win32-x64", true)]
    public void Maps_each_rid_to_its_npm_platform(string rid, string npmKey, bool isWindows)
    {
        var p = TsgoManifest.Platforms[rid];

        p.NpmKey.Should().Be(npmKey);
        p.IsWindows.Should().Be(isWindows);
        p.BinaryEntry.Should().Be(isWindows ? "lib/tsgo.exe" : "lib/tsgo");
        p.TarballUrl.Should().Be(
            $"https://registry.npmjs.org/@typescript/{npmKey}/-/{npmKey}-{TsgoManifest.Version}.tgz");
    }

    [Fact]
    public void Every_platform_carries_a_decodable_sha512_pin()
    {
        foreach (var (rid, p) in TsgoManifest.Platforms)
        {
            p.Sha512.Should().NotBeNullOrWhiteSpace($"rid {rid} needs a pin");
            // npm integrity is base64 of the raw 64-byte SHA-512 digest.
            Convert.FromBase64String(p.Sha512).Should().HaveCount(64, $"rid {rid}");
        }
    }

    [Fact]
    public void Covers_the_same_rids_as_esbuild()
    {
        // The two binaries are provisioned the same way for the same publish hosts, and
        // TsgoManifest.Current() resolves via EsbuildManifest.CurrentRid() - the RID sets must
        // not drift apart.
        TsgoManifest.Platforms.Keys.Should().BeEquivalentTo(EsbuildManifest.Platforms.Keys);
    }

    [Fact]
    public void Current_resolves_to_a_pinned_platform_on_this_host()
    {
        var current = TsgoManifest.Current();
        TsgoManifest.Platforms.Values.Should().Contain(current);
    }
}
