using System.Diagnostics;
using System.Runtime.InteropServices;
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

        // Fail-closed type-check gate (ui-components.md section 6): a publish must never
        // bundle a tree that does not type-check. Trivially clean before any .ts exists.
        if (!Typecheck(wwwroot))
        {
            return false;
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

        // Entry is app.ts once the SPA is migrated, app.js before then; esbuild bundles either
        // natively and resolves the still-".js" import specifiers to their ".ts" sources. The
        // strict checker tsconfig is NOT used here - esbuild only needs the throwaway baseUrl+paths
        // map above (it warns on options it does not understand); tsgo alone reads the strict one.
        var entry = Path.Combine(wwwroot, "app.ts");
        if (!File.Exists(entry))
        {
            entry = Path.Combine(wwwroot, "app.js");
        }

        return RunEsbuild(
            esbuild,
            [entry],
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

        return RunEsbuild(esbuild, [entry], "--bundle", "--minify", $"--outfile={outFile}");
    }

    private bool RunEsbuild(string esbuild, IReadOnlyList<string> entries, params string[] args)
    {
        var psi = new ProcessStartInfo(esbuild)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var e in entries)
        {
            psi.ArgumentList.Add(e);
        }

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"could not start esbuild: {esbuild}");
        // Drain both pipes concurrently so a large diagnostic on one can't deadlock the other.
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        var stderr = errTask.GetAwaiter().GetResult();
        outTask.GetAwaiter().GetResult();

        if (proc.ExitCode != 0)
        {
            var label = entries.Count == 1 ? Path.GetFileName(entries[0]) : $"{entries.Count} entries";
            _log.WriteLine($"bundle: esbuild failed (exit {proc.ExitCode}) for {label}:");
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
            // ".ts" covers BOTH migrated source and the van *.d.ts sidecars
            // (Path.GetExtension("van.d.ts") == ".ts"); neither the source nor the checker config
            // (tsconfig.json) should ship - the bundle inlined everything they describe.
            if (ext is ".js" or ".css" or ".br" or ".gz" or ".ts"
                || string.Equals(Path.GetFileName(path), "tsconfig.json", StringComparison.Ordinal))
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

    /// <summary>
    /// Run the pinned <c>tsgo</c> checker over <c>wwwroot/tsconfig.json</c> (<c>--noEmit</c>).
    /// Returns true when there are zero diagnostics (or no tsconfig yet). esbuild never type-checks;
    /// this is the only type gate. Used both standalone (<c>--typecheck</c>) and as <see cref="Run"/>'s
    /// fail-closed publish pre-step.
    /// </summary>
    public bool Typecheck(string wwwroot)
    {
        ArgumentNullException.ThrowIfNull(wwwroot);
        wwwroot = Path.GetFullPath(wwwroot);
        var tsconfig = Path.Combine(wwwroot, "tsconfig.json");
        if (!File.Exists(tsconfig))
        {
            _log.WriteLine($"typecheck: no tsconfig.json under {wwwroot}, nothing to check.");
            return true;
        }

        var tsgo = new TsgoBinary(_log).Ensure();
        return RunTsgo(tsgo, tsconfig);
    }

    private bool RunTsgo(string tsgo, string tsconfig)
    {
        // tsgo loads its sibling lib.*.d.ts from its own directory, so it MUST run in place (it is
        // already cached in place by TsgoBinary). We only pass the project + --noEmit.
        var psi = new ProcessStartInfo(tsgo)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(tsconfig);
        psi.ArgumentList.Add("--noEmit"); // belt-and-braces; the tsconfig already sets noEmit

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"could not start tsgo: {tsgo}");
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        var stdout = outTask.GetAwaiter().GetResult();
        var stderr = errTask.GetAwaiter().GetResult();

        if (proc.ExitCode != 0)
        {
            _log.WriteLine($"typecheck: tsgo reported diagnostics (exit {proc.ExitCode}):");
            if (stdout.Length > 0)
            {
                _log.WriteLine(stdout);
            }

            if (stderr.Length > 0)
            {
                _log.WriteLine(stderr);
            }

            return false;
        }

        _log.WriteLine("typecheck: tsgo clean (0 diagnostics).");
        return true;
    }

    /// <summary>
    /// Build the served dev mirror: copy <paramref name="src"/> (assets ride along) into
    /// <paramref name="outDir"/>, esbuild-transpile every <c>.ts</c> into a sibling <c>.js</c>
    /// (linked sourcemaps - sibling <c>.js.map</c> fetched only when devtools is open), then prune
    /// the <c>.ts</c>/<c>.d.ts</c> + checker config so only <c>.js</c>(<c>.map</c>) + assets are
    /// served. <paramref name="src"/> (wwwroot) stays source-only and untouched. esbuild does NOT
    /// type-check - <c>--typecheck</c> is the separate gate.
    /// </summary>
    public bool Transpile(string src, string outDir)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(outDir);
        src = Path.GetFullPath(src);
        outDir = Path.GetFullPath(outDir);
        if (!Directory.Exists(src))
        {
            _log.WriteLine($"transpile: source not found: {src}");
            return false;
        }

        // Guard the recursive delete below: refuse if outDir IS src or overlaps it (ancestor or
        // descendant), so a misinvocation like `--transpile wwwroot wwwroot` can never nuke source.
        var srcSlash = src + Path.DirectorySeparatorChar;
        var outSlash = outDir + Path.DirectorySeparatorChar;
        if (srcSlash.StartsWith(outSlash, StringComparison.Ordinal) ||
            outSlash.StartsWith(srcSlash, StringComparison.Ordinal))
        {
            _log.WriteLine($"transpile: refusing to build into {outDir}: it overlaps the source {src}.");
            return false;
        }

        // Fresh mirror each build so a stale prior emit never leaks (the served root is disposable).
        if (Directory.Exists(outDir))
        {
            Directory.Delete(outDir, recursive: true);
        }

        CopyDirectory(src, outDir);
        var tsconfigOut = Path.Combine(outDir, "tsconfig.json");
        if (File.Exists(tsconfigOut))
        {
            File.Delete(tsconfigOut); // the checker config is never served
        }

        var entries = TsEntryPoints(src);
        if (entries.Count == 0)
        {
            // Pre-migration (Stage 0): the copied raw .js graph is already servable as-is.
            _log.WriteLine("transpile: no .ts modules; serving the copied source unchanged.");
            DeleteByExtension(outDir, ".ts"); // remove any stray sidecar .d.ts
            return true;
        }

        var esbuild = new EsbuildBinary(_log).Ensure();
        if (!RunEsbuild(esbuild, entries, TranspileArgs(src, outDir)))
        {
            return false;
        }

        // Only .js + assets are served: drop every .ts (incl .d.ts sidecars) from the mirror.
        DeleteByExtension(outDir, ".ts");
        _log.WriteLine($"transpile: {entries.Count} module(s) {src} -> {outDir}");
        return true;
    }

    /// <summary>
    /// esbuild <c>--watch</c> over <paramref name="src"/>'s <c>.ts</c> into <paramref name="outDir"/>:
    /// re-transpiles on save for dev hot reload (real names + lines via linked maps). Blocks until
    /// the process is stopped. Assumes a prior <see cref="Transpile"/> already populated the mirror;
    /// non-<c>.js</c> asset edits still need a fresh <see cref="Transpile"/>.
    /// </summary>
    public bool Watch(string src, string outDir)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(outDir);
        src = Path.GetFullPath(src);
        outDir = Path.GetFullPath(outDir);

        var entries = TsEntryPoints(src);
        if (entries.Count == 0)
        {
            _log.WriteLine("transpile: no .ts modules to watch.");
            return true;
        }

        if (!Directory.Exists(outDir))
        {
            _log.WriteLine($"transpile: watch target {outDir} does not exist - run --transpile first.");
            return false;
        }

        var esbuild = new EsbuildBinary(_log).Ensure();
        return RunEsbuildWatch(esbuild, entries, TranspileArgs(src, outDir));
    }

    // Per-file transpile (NOT --bundle): each .ts -> a sibling .js, structure mirrored via
    // --outbase. Imports keep their ".js" specifiers, which resolve at the served URL.
    // --sourcemap=linked emits a sibling .js.map + a one-line sourceMappingURL comment (the
    // browser fetches the map ONLY when devtools is open), rather than =inline which base64-
    // embeds the whole original source into every served .js - that inflated the dev mirror
    // ~3.7x (sourcemaps were ~73% of the bytes the browser downloaded on every load).
    private static string[] TranspileArgs(string outbase, string outDir) =>
        [$"--outdir={outDir}", $"--outbase={outbase}", "--format=esm", "--sourcemap=linked"];

    // Every *.ts under root EXCEPT *.d.ts (declarations have no runtime emit; handing esbuild a
    // .d.ts entry would emit an empty .js).
    private static IReadOnlyList<string> TsEntryPoints(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.ts", SearchOption.AllDirectories)
                .Where(p => !p.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase))
                .ToArray()
            : [];

    private bool RunEsbuildWatch(string esbuild, IReadOnlyList<string> entries, IReadOnlyList<string> args)
    {
        // Inherit stdio so esbuild prints each rebuild's status live to the dev console.
        var psi = new ProcessStartInfo(esbuild) { UseShellExecute = false };
        foreach (var e in entries)
        {
            psi.ArgumentList.Add(e);
        }

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        psi.ArgumentList.Add("--watch");

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"could not start esbuild: {esbuild}");

        // `make run`/`make dev` background this process and SIGTERM it on Ctrl+C; that signal lands
        // on us, not the grandchild esbuild. Kill the whole child tree on SIGINT/SIGTERM and on
        // normal exit so no orphan --watch survives. (No-op for these signals on Windows.)
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, _ => KillTree(proc));
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => KillTree(proc));
        void OnExit(object? s, EventArgs e) => KillTree(proc);
        AppDomain.CurrentDomain.ProcessExit += OnExit;
        try
        {
            proc.WaitForExit();
            return proc.ExitCode == 0;
        }
        finally
        {
            AppDomain.CurrentDomain.ProcessExit -= OnExit;
        }
    }

    private static void KillTree(Process proc)
    {
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Already gone / not killable - nothing to clean up.
        }
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(src, dir)));
        }

        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(dest, Path.GetRelativePath(src, file)), overwrite: true);
        }
    }

    private static void DeleteByExtension(string root, string ext)
    {
        foreach (var path in Directory.EnumerateFiles(root, "*" + ext, SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetExtension(path), ext, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
            }
        }
    }

    private static string JsonString(string value) =>
        System.Text.Json.JsonSerializer.Serialize(value);
}
