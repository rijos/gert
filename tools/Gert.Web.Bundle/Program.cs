// Gert.Web.Bundle - the no-npm web toolchain driver (ui-components.md section 6).
// One small dispatcher over the two
// pinned Go binaries we fetch + SHA-512-verify from the npm registry (no npm, no Node):
//
//   Gert.Web.Bundle <wwwroot>                 bundle for RELEASE (esbuild): collapse the ESM
//                                             graph (app.ts|app.js entry) into one /app.js + the
//                                             four global sheets into /app.css, repoint
//                                             index.html, prune the raw source. Runs a fail-closed
//                                             tsgo --noEmit gate first. This is the Publish path
//                                             (Gert.Api.csproj BundleWebAssets) - kept as the bare
//                                             single-arg form so that target needs no change.
//   Gert.Web.Bundle --typecheck <wwwroot>     tsgo CHECKER gate only (no emit). Exit non-zero on
//                                             any diagnostic. This is `make typecheck` / the CI job.
//   Gert.Web.Bundle --transpile <src> <out>   DEV build: mirror <src> into <out>, esbuild-transpile
//                                             every .ts -> sibling .js (linked sourcemaps), prune
//                                             the .ts/.d.ts. The served mirror for the dev hosts.
//   Gert.Web.Bundle --watch <src> <out>       esbuild --watch over <src>'s .ts into <out> (assumes
//                                             a prior --transpile populated <out>): hot reload.
//
// Cache-busting note: app.js / app.css keep STABLE filenames so the entry tags resolve;
// bust via ETag (static files) + asp-append-version, never content-hashed names.
using Gert.Web.Bundle;

return Run(args);

static int Run(string[] args)
{
    try
    {
        switch (args)
        {
            case ["--typecheck", var wwwroot] when !string.IsNullOrWhiteSpace(wwwroot):
                return new Bundler(Console.Out).Typecheck(wwwroot) ? 0 : 1;

            case ["--transpile", var src, var outDir]
                when !string.IsNullOrWhiteSpace(src) && !string.IsNullOrWhiteSpace(outDir):
                return new Bundler(Console.Out).Transpile(src, outDir) ? 0 : 1;

            case ["--watch", var src, var outDir]
                when !string.IsNullOrWhiteSpace(src) && !string.IsNullOrWhiteSpace(outDir):
                return new Bundler(Console.Out).Watch(src, outDir) ? 0 : 1;

            // Bare <wwwroot> = the release bundle (the Publish target's contract). Fail-closed:
            // a failed bundle must break the publish, never ship a half-bundled graph.
            case [var wwwroot] when !string.IsNullOrWhiteSpace(wwwroot) && !wwwroot.StartsWith('-'):
                return new Bundler(Console.Out).Run(wwwroot) ? 0 : 1;

            default:
                return Usage();
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"bundle: {ex.Message}");
        return 1;
    }
}

static int Usage()
{
    Console.Error.WriteLine(
        """
        usage:
          Gert.Web.Bundle <wwwroot>                 bundle for release (publish)
          Gert.Web.Bundle --typecheck <wwwroot>     tsgo --noEmit gate (no emit)
          Gert.Web.Bundle --transpile <src> <out>   esbuild .ts -> .js mirror (dev)
          Gert.Web.Bundle --watch <src> <out>       esbuild --watch (dev hot reload)
        """);
    return 1;
}
