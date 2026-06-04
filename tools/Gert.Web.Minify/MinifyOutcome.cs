namespace Gert.Web.Minify;

/// <summary>
/// The result of attempting to minify one asset (ui-components.md §6). The
/// <b>no-bundle</b> design is the safety net: when a file fails to parse we keep
/// the raw bytes (<see cref="LeftRaw"/>) so the ESM import graph never breaks —
/// one file tripping the parser must not cascade.
/// </summary>
public enum MinifyOutcome
{
    /// <summary>The file was minified and overwritten in place (smaller, parses).</summary>
    Minified,

    /// <summary>NUglify reported errors (or produced empty/larger output) — left raw.</summary>
    LeftRaw,
}
