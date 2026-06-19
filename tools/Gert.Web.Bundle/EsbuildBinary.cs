using System.Formats.Tar;
using System.IO.Compression;

namespace Gert.Web.Bundle;

/// <summary>
/// Provisions the pinned esbuild binary (<see cref="EsbuildManifest"/>) for the current host:
/// download the registry tarball over HTTPS, verify its SHA-512, extract the single Go binary,
/// and cache it under the OS temp dir keyed by version+RID so repeat publishes are instant.
/// No npm, no Node - just an HTTPS GET of a tarball we've pinned (ui-components.md section 6).
/// </summary>
public sealed class EsbuildBinary(TextWriter log)
{
    private readonly TextWriter _log = log;

    /// <summary>
    /// Return the path to a verified esbuild executable, downloading + caching it on first use.
    /// Offline operators can pre-seed the cache dir this method logs.
    /// </summary>
    public string Ensure()
    {
        var rid = EsbuildManifest.CurrentRid();
        var platform = EsbuildManifest.Platforms[rid];

        var cacheDir = Path.Combine(
            Path.GetTempPath(), "gert-esbuild", EsbuildManifest.Version, rid);
        var exePath = Path.Combine(cacheDir, platform.ExecutableName);

        if (File.Exists(exePath))
        {
            _log.WriteLine($"esbuild: using cached {EsbuildManifest.Version} ({rid}) at {exePath}");
            return exePath;
        }

        Directory.CreateDirectory(cacheDir);
        _log.WriteLine($"esbuild: fetching {EsbuildManifest.Version} ({rid}) from {platform.TarballUrl}");

        var tarball = Download(platform.TarballUrl);
        TarballVerifier.VerifySha512(tarball, platform.Sha512, $"esbuild {platform.TarballUrl}");
        ExtractTo(tarball, platform.EntryPath, exePath);

        _log.WriteLine($"esbuild: cached at {exePath}");
        return exePath;
    }

    private static byte[] Download(string url)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        return http.GetByteArrayAsync(url).GetAwaiter().GetResult();
    }

    /// <summary>Extract exactly the pinned binary entry (skip the rest of the npm package) and mark it executable.</summary>
    private static void ExtractTo(byte[] tarball, string entryPath, string exePath)
    {
        using var gz = new GZipStream(new MemoryStream(tarball), CompressionMode.Decompress);
        using var tar = new TarReader(gz);

        for (var entry = tar.GetNextEntry(); entry is not null; entry = tar.GetNextEntry())
        {
            if (!string.Equals(entry.Name, entryPath, StringComparison.Ordinal) ||
                entry.DataStream is null)
            {
                continue;
            }

            // Write to a sibling temp file then move into place so a torn write never
            // leaves a half-extracted binary that a later run would treat as cached.
            var tmp = exePath + ".tmp";
            using (var dst = File.Create(tmp))
            {
                entry.DataStream.CopyTo(dst);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    tmp,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            File.Move(tmp, exePath, overwrite: true);
            return;
        }

        throw new InvalidOperationException($"esbuild tarball did not contain expected entry '{entryPath}'.");
    }
}
