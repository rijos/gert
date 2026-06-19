namespace Gert.Web.Bundle;

/// <summary>
/// The pinned <c>tsgo</c> release the type-check gate runs (ui-components.md section 6,
/// "Bumping tsgo"). <c>tsgo</c> is TypeScript 7's native Go checker; like esbuild it ships as a Go
/// binary inside an npm-registry tarball (the <c>@typescript/native-preview-{rid}</c> packages),
/// which we fetch directly over HTTPS and SHA-512-verify - <b>no npm, no Node</b> - so the
/// "no package manager" tenet still holds. It is a CHECKER ONLY (<c>--noEmit</c>); esbuild owns
/// all emit. The binary never ships; it only runs during <c>make typecheck</c> / publish.
///
/// <para><c>tsgo</c> is a daily-dev preview, so pin the EXACT version + per-RID SHA-512 for
/// reproducibility. To bump: pick a new <c>7.0.0-dev.YYYYMMDD.N</c>, fetch each RID's
/// <c>dist.integrity</c> from <c>https://registry.npmjs.org/@typescript/native-preview-&lt;rid&gt;</c>,
/// refresh all five pins, and re-run <c>make typecheck</c> + the suite.</para>
/// </summary>
public static class TsgoManifest
{
    /// <summary>The pinned tsgo version. Bump in lockstep with <see cref="Platforms"/> (all five pins).</summary>
    public const string Version = "7.0.0-dev.20260618.1";

    /// <summary>One entry per supported host: the npm platform package and its tarball pin.</summary>
    public sealed record Platform(string NpmKey, string Sha512, bool IsWindows)
    {
        /// <summary>The pinned tarball URL on the npm registry (a plain HTTPS GET - not `npm install`).</summary>
        public string TarballUrl =>
            $"https://registry.npmjs.org/@typescript/{NpmKey}/-/{NpmKey}-{Version}.tgz";

        /// <summary>
        /// Path of the tsgo binary INSIDE the extracted subtree (after the leading <c>package/</c>
        /// is stripped). The binary lives next to its <c>lib.*.d.ts</c> siblings under <c>lib/</c>
        /// and MUST be invoked in place - relocating it away from those siblings panics
        /// ("bundled: .../lib.d.ts does not exist; this executable may be misplaced").
        /// </summary>
        public string BinaryEntry => IsWindows ? "lib/tsgo.exe" : "lib/tsgo";
    }

    /// <summary>
    /// Pinned platforms, keyed by .NET RID (the same keys <see cref="EsbuildManifest"/> uses).
    /// SHA-512 values are the npm <c>dist.integrity</c> (base64, the <c>sha512-</c> prefix
    /// stripped) for <see cref="Version"/>.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Platform> Platforms =
        new Dictionary<string, Platform>(StringComparer.Ordinal)
        {
            ["linux-x64"] = new(
                "native-preview-linux-x64",
                "I41MDMS+a1f0DaFhXuHntzaltFgwFZDtkMDqfqRn43QodIqRsICtXuNDS+kg2b2rwFsA/MAWomdMiMaiLuZ9bw==",
                false),
            ["linux-arm64"] = new(
                "native-preview-linux-arm64",
                "VAiv3IlWOfxAQM9m05+Usv9qu62p2UWe3HN4L9jS+EvLOwB6LCaD0yZ1AiKTqtXoyFseOhyOiZ2gF6qgXfi7CQ==",
                false),
            ["osx-x64"] = new(
                "native-preview-darwin-x64",
                "YRP/kEP40j3WUDYB+tuV0yLY9CLDcYbNsnjoYFvK9sfabneKXV/10cmE1FdGb0ipIKhj6lIjt4BWsx4R6WP7fQ==",
                false),
            ["osx-arm64"] = new(
                "native-preview-darwin-arm64",
                "NcJ3qhu1zPeTIptjHV+I/7h8dKJRE4rQeWx4RH2AXDyyH95Njlyvh0FSkTZHZ0GjZ2/vagEGs/jfDwSXjCwfrw==",
                false),
            ["win-x64"] = new(
                "native-preview-win32-x64",
                "73ZJovElhT86jv5HlFTwZHqfOBdMtrqDjuLM2OZWmgYlw7pd6zuYipngGQDpYq0XBjqUzGHcWLQ7D7/jcjYNaQ==",
                true),
        };

    /// <summary>
    /// Resolve the pinned platform for the current host. Reuses
    /// <see cref="EsbuildManifest.CurrentRid"/> so the supported-RID set stays in lockstep with
    /// the esbuild provisioner (the two binaries are fetched the same way for the same hosts).
    /// </summary>
    public static Platform Current() => Platforms[EsbuildManifest.CurrentRid()];
}
