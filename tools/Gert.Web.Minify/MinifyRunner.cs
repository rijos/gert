namespace Gert.Web.Minify;

/// <summary>
/// Walks a target directory (the published <c>wwwroot</c>) and minifies every
/// <c>.js</c>/<c>.css</c> in place via <see cref="WebAssetMinifier"/>. Vendored libs
/// under <c>lib/</c> (van.js, van-x.js) are minified too - if NUglify trips on the
/// already-min source, the raw fallback keeps them intact (ui-components.md section 6).
/// </summary>
public sealed class MinifyRunner(TextWriter log)
{
    private readonly TextWriter _log = log;

    /// <summary>
    /// Minify all <c>.js</c>/<c>.css</c> under <paramref name="root"/> in place. Logs one
    /// line per file left raw and returns the aggregate <see cref="MinifySummary"/>.
    /// </summary>
    public MinifySummary Run(string root)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (!Directory.Exists(root))
        {
            _log.WriteLine($"minify: target directory not found, nothing to do: {root}");
            return default;
        }

        var minified = 0;
        var leftRaw = 0;
        long bytesSaved = 0;

        foreach (var path in EnumerateAssets(root))
        {
            var result = WebAssetMinifier.MinifyFileInPlace(path);
            if (result.Outcome == MinifyOutcome.Minified)
            {
                minified++;
                bytesSaved += result.BytesSaved;
            }
            else
            {
                leftRaw++;
                _log.WriteLine(
                    $"minify: left raw (fallback): {Relative(root, path)}"
                    + (result.Reason is null ? string.Empty : $" - {result.Reason}"));
            }
        }

        return new MinifySummary(minified, leftRaw, bytesSaved);
    }

    /// <summary>Enumerate the minifiable assets under <paramref name="root"/> (.js and .css).</summary>
    private static IEnumerable<string> EnumerateAssets(string root) =>
        Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(p =>
            {
                var ext = Path.GetExtension(p).ToLowerInvariant();
                return ext is ".js" or ".css";
            });

    private static string Relative(string root, string path)
    {
        var rel = Path.GetRelativePath(root, path);
        return rel.Replace(Path.DirectorySeparatorChar, '/');
    }
}
