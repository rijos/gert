namespace Gert.Web.Minify;

/// <summary>Aggregate result of a directory walk: how many files minified vs. left raw, and bytes saved.</summary>
/// <param name="Minified">Count of files minified in place.</param>
/// <param name="LeftRaw">Count of files left raw (fallback — parser error / not smaller).</param>
/// <param name="BytesSaved">Total bytes saved across all minified files.</param>
public readonly record struct MinifySummary(int Minified, int LeftRaw, long BytesSaved)
{
    /// <summary>Total <c>.js</c>/<c>.css</c> files considered.</summary>
    public int Total => Minified + LeftRaw;
}
