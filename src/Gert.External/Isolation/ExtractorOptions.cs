namespace Gert.External.Isolation;

/// <summary>
/// Resource caps + hardening knobs for the isolated PDF/DOCX extractor (security F7).
/// Bound from configuration section <c>Gert:Extractor</c>. All non-secret. The defaults
/// are the locked-down posture: capped address space / CPU / process count, a wall-clock
/// kill, no network, DTD/external-entities off, and decompressed-size + zip-entry caps
/// for the DOCX zip.
/// </summary>
public sealed class ExtractorOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Gert:Extractor";

    /// <summary>Path to the unprivileged extractor helper executable.</summary>
    public string HelperPath { get; set; } = "gert-extract";

    /// <summary>Address-space cap (RLIMIT_AS) in MiB.</summary>
    public int AddressSpaceMiB { get; set; } = 512;

    /// <summary>CPU-time cap (RLIMIT_CPU) in seconds.</summary>
    public int CpuSeconds { get; set; } = 20;

    /// <summary>Process/thread cap (RLIMIT_NPROC).</summary>
    public int ProcessLimit { get; set; } = 16;

    /// <summary>Wall-clock kill timeout (seconds) - backstops RLIMIT_CPU.</summary>
    public int WallClockSeconds { get; set; } = 30;

    /// <summary>Unprivileged uid the helper drops to (nobody-class).</summary>
    public int RunAsUid { get; set; } = 65534;

    /// <summary>Max decompressed size across all DOCX zip entries (bytes) - bomb cap.</summary>
    public long MaxDecompressedBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>Max number of entries in the DOCX zip - bomb cap.</summary>
    public int MaxZipEntries { get; set; } = 2048;

    /// <summary>Cap on emitted extracted text (bytes).</summary>
    public long MaxOutputBytes { get; set; } = 16L * 1024 * 1024;
}
