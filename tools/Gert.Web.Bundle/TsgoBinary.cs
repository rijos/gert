using System.Formats.Tar;
using System.IO.Compression;

namespace Gert.Web.Bundle;

/// <summary>
/// Provisions the pinned <c>tsgo</c> binary (<see cref="TsgoManifest"/>) for the current host:
/// download the registry tarball over HTTPS, verify its SHA-512, and extract it - no npm, no Node
/// (ui-components.md section 6, "Bumping tsgo").
///
/// <para>Unlike esbuild (a single self-contained binary), <c>tsgo</c> loads its sibling
/// <c>lib.*.d.ts</c> from the directory containing the executable - relocate the binary away from
/// that <c>lib/</c> and it panics. So we extract the WHOLE <c>package/</c> subtree (stripping the
/// leading <c>package/</c>), preserving the <c>lib/tsgo</c> + <c>lib/lib.*.d.ts</c> layout, and
/// invoke the binary IN PLACE. A <c>.extracted</c> sentinel - written only after the multi-file
/// extraction has fully landed - gates the cache hit, so a torn extraction is never mistaken for a
/// complete one.</para>
/// </summary>
public sealed class TsgoBinary(TextWriter log)
{
    private readonly TextWriter _log = log;

    /// <summary>
    /// Return the path to a verified, in-place <c>tsgo</c> executable, downloading + caching it on
    /// first use. Offline operators can pre-seed the cache dir this method logs.
    /// </summary>
    public string Ensure()
    {
        var rid = EsbuildManifest.CurrentRid();
        var platform = TsgoManifest.Platforms[rid];

        var versionDir = Path.Combine(Path.GetTempPath(), "gert-tsgo", TsgoManifest.Version);
        var cacheDir = Path.Combine(versionDir, rid);
        var sentinel = Path.Combine(cacheDir, ".extracted");
        var binaryPath = Path.Combine(cacheDir, ToOsPath(platform.BinaryEntry));

        // The sentinel - not merely the binary's existence - proves the full subtree (binary +
        // every lib.*.d.ts) landed. Without it tsgo would panic on a missing lib at run time.
        if (File.Exists(sentinel) && File.Exists(binaryPath))
        {
            _log.WriteLine($"tsgo: using cached {TsgoManifest.Version} ({rid}) at {binaryPath}");
            return binaryPath;
        }

        Directory.CreateDirectory(versionDir);
        _log.WriteLine($"tsgo: fetching {TsgoManifest.Version} ({rid}) from {platform.TarballUrl}");

        var tarball = Download(platform.TarballUrl);
        TarballVerifier.VerifySha512(tarball, platform.Sha512, $"tsgo {platform.TarballUrl}");

        // Extract into a sibling staging dir, then atomically rename it into place. A torn extraction
        // never leaves a half-populated cacheDir that a later run could treat as complete.
        var staging = Path.Combine(versionDir, ".staging-" + Guid.NewGuid().ToString("N"));
        ExtractSubtree(tarball, staging);

        var stagedBinary = Path.Combine(staging, ToOsPath(platform.BinaryEntry));
        if (!File.Exists(stagedBinary))
        {
            Directory.Delete(staging, recursive: true);
            throw new InvalidOperationException(
                $"tsgo tarball did not contain expected binary '{platform.BinaryEntry}'.");
        }

        MarkExecutable(stagedBinary);

        // Write the sentinel INTO staging so the atomic rename below publishes the binary, its
        // lib/ siblings, and the sentinel together. There is then no instant where cacheDir looks
        // complete but lacks its sentinel, so a peer's cache-hit check above can never be satisfied
        // by a half-populated dir.
        File.WriteAllText(Path.Combine(staging, ".extracted"), TsgoManifest.Version);

        PublishAtomically(staging, cacheDir, sentinel, binaryPath);

        _log.WriteLine($"tsgo: cached at {binaryPath}");
        return binaryPath;
    }

    /// <summary>
    /// Atomically rename <paramref name="staging"/> into <paramref name="cacheDir"/>. Never deletes
    /// a cache a peer has already validly published (sentinel + binary present) - another process's
    /// tsgo may be reading its <c>lib/</c> right now; only a torn/incomplete cacheDir (no valid
    /// sentinel) is replaced. Tolerates a peer winning the race between our delete and our move.
    /// </summary>
    private static void PublishAtomically(string staging, string cacheDir, string sentinel, string binaryPath)
    {
        if (File.Exists(sentinel) && File.Exists(binaryPath))
        {
            Directory.Delete(staging, recursive: true); // a peer already published; use theirs
            return;
        }

        if (Directory.Exists(cacheDir))
        {
            Directory.Delete(cacheDir, recursive: true); // torn/stale: no valid sentinel
        }

        try
        {
            Directory.Move(staging, cacheDir);
        }
        catch (IOException) when (File.Exists(sentinel) && File.Exists(binaryPath))
        {
            Directory.Delete(staging, recursive: true); // a peer won the race; use theirs
        }
    }

    private static byte[] Download(string url)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        return http.GetByteArrayAsync(url).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Extract every file entry under the tarball's leading <c>package/</c> directory into
    /// <paramref name="staging"/>, preserving the relative layout (so <c>lib/tsgo</c> keeps its
    /// <c>lib/lib.*.d.ts</c> siblings). Guards against path traversal (zip-slip): an entry that
    /// resolves outside <paramref name="staging"/> aborts the extraction.
    /// </summary>
    private static void ExtractSubtree(byte[] tarball, string staging)
    {
        const string prefix = "package/";
        var stagingFull = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(staging);

        using var gz = new GZipStream(new MemoryStream(tarball), CompressionMode.Decompress);
        using var tar = new TarReader(gz);

        for (var entry = tar.GetNextEntry(); entry is not null; entry = tar.GetNextEntry())
        {
            if (!entry.Name.StartsWith(prefix, StringComparison.Ordinal) || entry.DataStream is null)
            {
                continue; // skip the leading dir entry, anything outside package/, and non-file entries
            }

            var relative = ToOsPath(entry.Name[prefix.Length..]);
            if (relative.Length == 0)
            {
                continue;
            }

            var dest = Path.GetFullPath(Path.Combine(staging, relative));
            if (!dest.StartsWith(stagingFull, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"tsgo tarball entry '{entry.Name}' escapes the extraction root (path traversal).");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var dst = File.Create(dest);
            entry.DataStream.CopyTo(dst);
        }
    }

    private static void MarkExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    /// <summary>Translate a tarball's forward-slash relative path to the host separator.</summary>
    private static string ToOsPath(string relative) =>
        relative.Replace('/', Path.DirectorySeparatorChar);
}
