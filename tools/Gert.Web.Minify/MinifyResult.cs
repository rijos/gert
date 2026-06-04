namespace Gert.Web.Minify;

/// <summary>
/// Per-file minification result: the <see cref="Outcome"/> plus the byte counts so
/// the caller can report bytes-saved and which files fell back to raw.
/// </summary>
/// <param name="Outcome">Whether the file was minified or left raw (fallback).</param>
/// <param name="OriginalBytes">Length of the source content in bytes.</param>
/// <param name="ResultBytes">
/// Length of what was written: the minified bytes when <see cref="Outcome"/> is
/// <see cref="MinifyOutcome.Minified"/>, otherwise equal to <see cref="OriginalBytes"/>.
/// </param>
public readonly record struct MinifyResult(
    MinifyOutcome Outcome,
    int OriginalBytes,
    int ResultBytes)
{
    /// <summary>Bytes saved (never negative — a non-shrinking result is left raw).</summary>
    public int BytesSaved => OriginalBytes - ResultBytes;
}
