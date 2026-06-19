using System.Runtime.InteropServices;

namespace Gert.Web.Bundle;

/// <summary>
/// The pinned esbuild release the publish bundler runs (ui-components.md section 6).
/// esbuild ships as a single static Go binary inside its npm-registry tarball; we fetch
/// that tarball directly over HTTPS and verify it - <b>no npm, no Node</b> - so the
/// "no package manager" tenet (section 1) still holds. Pin both the version AND the
/// per-platform SHA-512 (npm "integrity" form) so the download is reproducible and
/// tamper-evident; the binary never ships, it only runs during <c>dotnet publish</c>.
/// </summary>
public static class EsbuildManifest
{
    /// <summary>The pinned esbuild version. Bump in lockstep with <see cref="Platforms"/>.</summary>
    public const string Version = "0.28.1";

    /// <summary>One entry per supported publish host: the npm platform package and its tarball pin.</summary>
    public sealed record Platform(string NpmKey, string Sha512, bool IsWindows)
    {
        /// <summary>The pinned tarball URL on the npm registry (a plain HTTPS GET - not `npm install`).</summary>
        public string TarballUrl =>
            $"https://registry.npmjs.org/@esbuild/{NpmKey}/-/{NpmKey}-{Version}.tgz";

        /// <summary>Path of the binary inside the tarball (npm lays it out under <c>package/</c>).</summary>
        public string EntryPath => IsWindows ? "package/esbuild.exe" : "package/bin/esbuild";

        /// <summary>On-disk name for the extracted binary.</summary>
        public string ExecutableName => IsWindows ? "esbuild.exe" : "esbuild";
    }

    /// <summary>
    /// Pinned platforms, keyed by .NET RID. SHA-512 values are the npm <c>dist.integrity</c>
    /// (base64, the <c>sha512-</c> prefix stripped) for <see cref="Version"/>.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Platform> Platforms =
        new Dictionary<string, Platform>(StringComparer.Ordinal)
        {
            ["linux-x64"] = new(
                "linux-x64",
                "u/anNYF2mmVOEDwLtnQ1wOr3EZ9sTNGLWrsYGYwHWzGA3Si84IOkHXlbWTD1NB+9/1lcnweYKO54uhxZydNzfA==",
                false),
            ["linux-arm64"] = new(
                "linux-arm64",
                "yHs+0uc8+nvEAfAfxrWQKK5peSNzBc4PegcMO0EJ2hT71uA7vB8Ihg2e77R2P7SG5uYjPbHlLLmve4LLLRCf0g==",
                false),
            ["osx-x64"] = new(
                "darwin-x64",
                "zfdzgK9ACBNZLI/CyHTOx81SyNbM6YXn7rxSgX97VjyiPl9W1i4Ka4fgKECEoFCKGpvBj5qArWIGgQjOwkgskQ==",
                false),
            ["osx-arm64"] = new(
                "darwin-arm64",
                "TZbWkQY7kvTAXbXUT7uVACR5cMHsDiSz9z7ZKAX/RTq/WJEk3QyRr0wZpNhBDX+/0CtdqUIJlOiodQcta6tY3Q==",
                false),
            ["win-x64"] = new(
                "win32-x64",
                "bm4Mowrv+GXMlpWX++EcXw/iLyd1o3+bJkC2DkWXYVvgZCqD/bSj9ctZeAMC3cIxgjRVR2Dufaiu4YPxr5gW1A==",
                true),
        };

    /// <summary>
    /// The .NET RID for the current publish host (the subset esbuild + this manifest cover).
    /// Throws <see cref="PlatformNotSupportedException"/> for anything unpinned.
    /// </summary>
    public static string CurrentRid()
    {
        var arch = RuntimeInformation.OSArchitecture;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return arch switch
            {
                Architecture.X64 => "linux-x64",
                Architecture.Arm64 => "linux-arm64",
                _ => throw Unsupported("linux", arch),
            };
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return arch switch
            {
                Architecture.X64 => "osx-x64",
                Architecture.Arm64 => "osx-arm64",
                _ => throw Unsupported("macOS", arch),
            };
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && arch == Architecture.X64)
        {
            return "win-x64";
        }

        throw Unsupported(RuntimeInformation.OSDescription, arch);
    }

    /// <summary>Resolve the pinned platform for the current host.</summary>
    public static Platform Current() => Platforms[CurrentRid()];

    private static PlatformNotSupportedException Unsupported(string os, Architecture arch) =>
        // CurrentRid() now feeds BOTH the esbuild bundler and the tsgo checker, so name both
        // manifests - a RID added to only one would KeyNotFoundException in the other.
        new($"the web toolchain is not pinned for {os}/{arch}. Add the RID to BOTH " +
            "EsbuildManifest.Platforms and TsgoManifest.Platforms.");
}
