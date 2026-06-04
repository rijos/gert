// Gert.Web.Minify — NUglify minify-in-place for the no-npm release pipeline
// (ui-components.md §6). Invoked from Gert.Api.csproj's MinifyWebAssets target
// (AfterTargets=Publish, Release only) against "$(PublishDir)wwwroot".
//
// Usage: Gert.Web.Minify <target-dir>
//
// Cache-busting note: filenames stay STABLE so the ESM import graph + import map
// keep resolving. Bust via ETag (static files) + asp-append-version on the
// index.html entry tags — never content-hashed names (that breaks relative imports).
using Gert.Web.Minify;

if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
{
    Console.Error.WriteLine("usage: Gert.Web.Minify <target-dir>");
    return 1;
}

var root = args[0];
var runner = new MinifyRunner(Console.Out);
var summary = runner.Run(root);

Console.Out.WriteLine(
    $"minify: {summary.Minified} minified, {summary.LeftRaw} left-raw, " +
    $"{summary.BytesSaved} bytes saved ({summary.Total} assets).");

// Always succeed: a per-file fallback is not a build failure (no-bundle safety net).
return 0;
