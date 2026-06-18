// Gert.Web.Bundle - the no-npm release bundler (ui-components.md section 6).
// Invoked from Gert.Api.csproj's BundleWebAssets target (AfterTargets=Publish,
// Release only) against "$(PublishDir)wwwroot".
//
// Usage: Gert.Web.Bundle <wwwroot-dir>
//
// Runs the pinned esbuild Go binary (fetched + SHA-512-verified from the npm registry
// tarball - no npm, no Node) to collapse the ESM graph into one /app.js and the four
// global sheets into one /app.css, repoint index.html, and prune the raw source.
//
// Cache-busting note: app.js / app.css keep STABLE filenames so the entry tags resolve;
// bust via ETag (static files) + asp-append-version, never content-hashed names.
using Gert.Web.Bundle;

if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
{
    Console.Error.WriteLine("usage: Gert.Web.Bundle <wwwroot-dir>");
    return 1;
}

try
{
    // Fail-closed: a failed bundle must break the publish, never ship a half-bundled graph.
    return new Bundler(Console.Out).Run(args[0]) ? 0 : 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"bundle: {ex.Message}");
    return 1;
}
