using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Gert.Web.Bundle;

/// <summary>
/// The publish bundler (ui-components.md section 6). Runs esbuild over the published
/// <c>wwwroot</c>: the native-ESM graph rooted at <c>app.js</c> collapses into one minified
/// <c>/app.js</c>, the four global sheets into one minified <c>/app.css</c>, <c>index.html</c>
/// is repointed at the bundle, and the now-inlined raw <c>.js</c>/<c>.css</c> source is pruned.
/// Dev/Debug still serves the raw graph unchanged; bundling is Release-only and fail-closed -
/// if esbuild errors, <c>wwwroot</c> is left untouched (still a working raw graph) and the tool
/// exits non-zero, because a half-bundled graph must never ship.
/// </summary>
public sealed partial class Bundler(TextWriter log)
{
    private readonly TextWriter _log = log;

    // The four global sheets, in the cascade order index.html links them (tokens first so the
    // custom properties exist before any rule references them - ui-components.md section 2).
    private static readonly string[] GlobalSheets =
        ["styles/tokens.css", "styles/base.css", "styles/layout.css", "styles/primitives.css"];

    /// <summary>Bundle <paramref name="wwwroot"/> in place. Returns true on success.</summary>
    public bool Run(string wwwroot)
    {
        ArgumentNullException.ThrowIfNull(wwwroot);
        wwwroot = Path.GetFullPath(wwwroot);

        if (!Directory.Exists(wwwroot))
        {
            _log.WriteLine($"bundle: target directory not found, nothing to do: {wwwroot}");
            return true;
        }

        var esbuild = new EsbuildBinary(_log).Ensure();

        // All scratch (tsconfig, css entry, bundle outputs) lives OUTSIDE wwwroot so the
        // prune step can't see it, and so a failed bundle leaves the raw graph intact
        // (fail-closed). Only prune + swap once both outputs exist.
        var work = Path.Combine(Path.GetTempPath(), "gert-bundle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var jsOut = Path.Combine(work, "app.js");
            var cssOut = Path.Combine(work, "app.css");

            if (!BundleJs(esbuild, wwwroot, work, jsOut) || !BundleCss(esbuild, wwwroot, work, cssOut))
            {
                return false;
            }

            PrunePublishedAssets(wwwroot);
            File.Move(jsOut, Path.Combine(wwwroot, "app.js"), overwrite: true);
            File.Move(cssOut, Path.Combine(wwwroot, "app.css"), overwrite: true);
            RewriteIndexHtmlFile(Path.Combine(wwwroot, "index.html"));

            _log.WriteLine("bundle: app.js + app.css written, index.html repointed, raw source pruned.");
            return true;
        }
        finally
        {
            if (Directory.Exists(work))
            {
                Directory.Delete(work, recursive: true);
            }
        }
    }

    /// <summary>
    /// Bundle the ESM graph rooted at <c>app.js</c>. The graph mixes relative specifiers with a
    /// few absolute same-origin ones (e.g. <c>/lib/van.js</c>); a throwaway tsconfig maps
    /// <c>/*</c> back to <c>wwwroot</c> so esbuild resolves them without touching the source.
    /// </summary>
    private bool BundleJs(string esbuild, string wwwroot, string work, string outFile)
    {
        var tsconfig = Path.Combine(work, "tsconfig.json");
        File.WriteAllText(
            tsconfig,
            "{\"compilerOptions\":{\"baseUrl\":" + JsonString(wwwroot) + ",\"paths\":{\"/*\":[\"./*\"]}}}");

        return RunEsbuild(
            esbuild,
            Path.Combine(wwwroot, "app.js"),
            "--bundle", "--minify", "--format=esm", $"--tsconfig={tsconfig}", $"--outfile={outFile}");
    }

    /// <summary>Bundle the four global sheets, in order, into one minified <c>app.css</c>.</summary>
    private bool BundleCss(string esbuild, string wwwroot, string work, string outFile)
    {
        // Absolute @import paths so the entry can live outside wwwroot (the prune can't see it).
        var entry = Path.Combine(work, "entry.css");
        var imports = string.Join(
            "\n", GlobalSheets.Select(s => $"@import {JsonString(Path.Combine(wwwroot, s))};"));
        File.WriteAllText(entry, imports + "\n");

        return RunEsbuild(esbuild, entry, "--bundle", "--minify", $"--outfile={outFile}");
    }

    private bool RunEsbuild(string esbuild, string entry, params string[] args)
    {
        var psi = new ProcessStartInfo(esbuild)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(entry);
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"could not start esbuild: {esbuild}");
        var stderr = proc.StandardError.ReadToEnd();
        proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
        {
            _log.WriteLine($"bundle: esbuild failed (exit {proc.ExitCode}) for {Path.GetFileName(entry)}:");
            _log.WriteLine(stderr);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Delete every raw <c>.js</c>/<c>.css</c> under <paramref name="wwwroot"/> (the bundle inlines
    /// them all) plus every <c>.br</c>/<c>.gz</c> the publish pipeline pre-compressed. The host serves
    /// via classic <c>UseStaticFiles</c>, which does no <c>.br</c>/<c>.gz</c> negotiation, so those
    /// copies are never served - and post-bundle they are stale (e.g. <c>app.js.br</c> would still hold
    /// the un-bundled bootstrap), so leaving them would be a landmine for any future MapStaticAssets.
    /// </summary>
    private static void PrunePublishedAssets(string wwwroot)
    {
        foreach (var path in Directory.EnumerateFiles(wwwroot, "*.*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".js" or ".css" or ".br" or ".gz")
            {
                File.Delete(path);
            }
        }

        // Remove any directories the prune emptied (e.g. lib/, styles/, components/...).
        foreach (var dir in Directory.EnumerateDirectories(wwwroot, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }
    }

    private void RewriteIndexHtmlFile(string indexHtml)
    {
        if (!File.Exists(indexHtml))
        {
            _log.WriteLine($"bundle: index.html not found, skipping repoint: {indexHtml}");
            return;
        }

        File.WriteAllText(indexHtml, RewriteIndexHtml(File.ReadAllText(indexHtml)));
    }

    /// <summary>
    /// Replace the global <c>/styles/*.css</c> links with the single bundled <c>/app.css</c> link,
    /// keeping the first link's position. The <c>&lt;script src="/app.js"&gt;</c> tag is unchanged -
    /// the bundle keeps that stable filename so the entry tag and ETag cache-busting still resolve.
    /// Pure + static so it is unit-testable without a filesystem.
    /// </summary>
    public static string RewriteIndexHtml(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        var seenFirst = false;
        var rewritten = StylesLinkRegex().Replace(html, m =>
        {
            // Drop the extra links entirely (whole line); fold the first into the bundle
            // link, keeping its indentation so the document's shape is unchanged.
            if (seenFirst)
            {
                return string.Empty;
            }

            seenFirst = true;
            return $"{m.Groups["indent"].Value}<link rel=\"stylesheet\" href=\"/app.css\" />\n";
        });

        if (!seenFirst)
        {
            throw new InvalidOperationException(
                "index.html had no <link href=\"/styles/*.css\"> to repoint at the bundle.");
        }

        return rewritten;
    }

    /// <summary>A full-line stylesheet link whose href targets a global sheet under <c>/styles/</c>.</summary>
    [GeneratedRegex("""(?<indent>[ \t]*)<link\b[^>]*href="/styles/[^"]*"[^>]*>[ \t]*\r?\n""", RegexOptions.IgnoreCase)]
    private static partial Regex StylesLinkRegex();

    private static string JsonString(string value) =>
        System.Text.Json.JsonSerializer.Serialize(value);
}
