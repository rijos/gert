using NUglify;
using NUglify.Css;
using NUglify.JavaScript;

namespace Gert.Web.Minify;

/// <summary>
/// Pure-.NET (NUglify) minifier for the no-npm release pipeline (ui-components.md section 6).
/// Minifies each <c>.js</c>/<c>.css</c> <b>to the same relative path</b>, never bundling
/// or renaming, so the native-ESM import graph and the import map keep resolving.
///
/// <para>
/// <b>ESM-safe:</b> JavaScript is parsed in module mode so <c>import</c>/<c>export</c>,
/// arrow functions, template literals, and optional chaining survive. If a file fails to
/// minify - NUglify reports errors, or the output is empty / not smaller - the raw source
/// is kept (the <b>no-bundle</b> design is the safety net: a single file failing must never
/// break the import graph).
/// </para>
/// </summary>
public static class WebAssetMinifier
{
    /// <summary>
    /// JavaScript settings tuned for native ES modules: parse as a module program (so
    /// top-level <c>import</c>/<c>export</c> are legal) and never rename/remove unused
    /// top-level names - exports are the public surface other modules import.
    /// </summary>
    private static CodeSettings JsSettings => new()
    {
        // Module, not Program: in script mode top-level import/export are parse
        // errors, which silently left every ESM source raw (the 2026-06 finding -
        // only vendor/CSS assets ever minified).
        SourceMode = JavaScriptSourceMode.Module,
        // Do not rename anything: exported identifiers are imported by name in other
        // files, and we minify each file independently (no cross-file analysis).
        LocalRenaming = LocalRenaming.KeepAll,
        PreserveImportantComments = false,
    };

    private static CssSettings CssSettings => new()
    {
        CommentMode = CssComment.None,
    };

    /// <summary>
    /// Minify <paramref name="source"/> for the given file <paramref name="extension"/>
    /// (<c>.js</c> or <c>.css</c>, case-insensitive). Returns the minified text and a
    /// <see cref="MinifyOutcome.Minified"/> outcome on success; on any parser error, empty
    /// output, or non-shrinking output, returns the original <paramref name="source"/> with
    /// <see cref="MinifyOutcome.LeftRaw"/>.
    /// </summary>
    public static (string Content, MinifyOutcome Outcome) Minify(string source, string extension)
    {
        var (content, outcome, _) = MinifyWithReason(source, extension);
        return (content, outcome);
    }

    /// <summary>
    /// <see cref="Minify(string, string)"/> plus the fallback reason - the first
    /// parser error or the shrink check - so callers can LOG why a file stayed
    /// raw instead of hiding a systematic failure behind "fallback".
    /// </summary>
    public static (string Content, MinifyOutcome Outcome, string? Reason) MinifyWithReason(
        string source,
        string extension)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(extension);

        UglifyResult result = extension.ToLowerInvariant() switch
        {
            ".js" => Uglify.Js(source, JsSettings),
            ".css" => Uglify.Css(source, CssSettings),
            _ => default,
        };

        // Unknown extension, parser errors, or empty output -> keep raw.
        if (result.HasErrors || string.IsNullOrEmpty(result.Code))
        {
            var first = result.Errors is { Count: > 0 } ? result.Errors[0].ToString() : "no output";
            return (source, MinifyOutcome.LeftRaw, first);
        }

        // Never grow a file (already-minified vendor libs can come back equal or larger).
        if (result.Code.Length >= source.Length)
        {
            return (source, MinifyOutcome.LeftRaw, "did not shrink");
        }

        return (result.Code, MinifyOutcome.Minified, null);
    }

    /// <summary>
    /// Minify one file in place: read it, run <see cref="Minify(string, string)"/>, and
    /// overwrite only when the result actually minified. Defensive - any I/O or parser
    /// exception leaves the file untouched and reports <see cref="MinifyOutcome.LeftRaw"/>.
    /// </summary>
    public static MinifyResult MinifyFileInPlace(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        string source;
        try
        {
            source = File.ReadAllText(path);
        }
        catch (IOException)
        {
            return new MinifyResult(MinifyOutcome.LeftRaw, 0, 0);
        }

        var originalBytes = System.Text.Encoding.UTF8.GetByteCount(source);
        var extension = Path.GetExtension(path);

        string content;
        MinifyOutcome outcome;
        string? reason;
        try
        {
            (content, outcome, reason) = MinifyWithReason(source, extension);
        }
        catch (Exception ex)
        {
            // A NUglify internal failure must never abort the publish or corrupt the file.
            return new MinifyResult(MinifyOutcome.LeftRaw, originalBytes, originalBytes, ex.Message);
        }

        if (outcome != MinifyOutcome.Minified)
        {
            return new MinifyResult(MinifyOutcome.LeftRaw, originalBytes, originalBytes, reason);
        }

        try
        {
            File.WriteAllText(path, content);
        }
        catch (IOException ex)
        {
            return new MinifyResult(MinifyOutcome.LeftRaw, originalBytes, originalBytes, ex.Message);
        }

        return new MinifyResult(
            MinifyOutcome.Minified,
            originalBytes,
            System.Text.Encoding.UTF8.GetByteCount(content));
    }
}
