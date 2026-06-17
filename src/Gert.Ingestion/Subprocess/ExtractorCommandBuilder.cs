namespace Gert.Ingestion.Subprocess;

/// <summary>
/// Pure builder for the unprivileged extractor-subprocess invocation (security F7).
/// Assembles the helper argument vector carrying the resource caps (RLIMIT_AS / CPU /
/// NPROC), the drop-privs uid, the no-network flag, and the input file path. Kept
/// network- and process-free so the locked-down posture is unit-assertable.
///
/// <para>
/// <b>Integration-only:</b> the actual spawn of the helper (which applies the rlimits +
/// setuid, then parses with PdfPig/OpenXML and emits text/JSON to stdout) runs only when
/// the helper exists; CI asserts the argument list, not a live child.
/// </para>
/// </summary>
public static class ExtractorCommandBuilder
{
    /// <summary>
    /// Build the helper argument vector. The helper is expected to apply the rlimits
    /// and drop to <see cref="ExtractorParameters.RunAsUid"/> <b>itself</b> (so the caps
    /// are enforced in-child before it touches the bytes); we pass them explicitly so
    /// the policy is one place and testable.
    /// </summary>
    public static IReadOnlyList<string> BuildArgs(ExtractorParameters options, string extension, string inputPath)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(extension);
        ArgumentException.ThrowIfNullOrEmpty(inputPath);

        return new List<string>
        {
            "--type", extension,
            "--input", inputPath,
            "--uid", options.RunAsUid.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--rlimit-as-mib", options.AddressSpaceMiB.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--rlimit-cpu", options.CpuSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--rlimit-nproc", options.ProcessLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--max-decompressed", options.MaxDecompressedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--max-zip-entries", options.MaxZipEntries.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--max-output", options.MaxOutputBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            // No network for the extractor (F7).
            "--no-network",
        };
    }
}
