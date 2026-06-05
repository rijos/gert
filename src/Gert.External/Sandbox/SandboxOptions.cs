namespace Gert.External.Sandbox;

/// <summary>
/// Limits + wiring for the gVisor (<c>runsc</c>) Python sandbox (chat-and-tools.md
/// § sandbox; security F5). Bound from configuration section <c>Gert:Sandbox</c>. All
/// non-secret. The defaults are the security posture: <b>egress off</b>, read-only
/// rootfs, no <c>/data</c> mount, hard CPU/mem/PID/wall caps.
/// </summary>
public sealed class SandboxOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Gert:Sandbox";

    /// <summary>Path to the <c>runsc</c> binary.</summary>
    public string RunscPath { get; set; } = "runsc";

    /// <summary>Container image / OCI bundle root with a Python runtime.</summary>
    public string Image { get; set; } = "gert-sandbox-python";

    /// <summary>
    /// Outbound network. <b>Off by default</b> — the exfiltration brake for arbitrary
    /// code (F5). An allow-list is opt-in only and never the default.
    /// </summary>
    public bool EgressEnabled { get; set; }

    /// <summary>Wall-clock kill timeout for a run (seconds).</summary>
    public int WallClockSeconds { get; set; } = 10;

    /// <summary>CPU-time limit (seconds).</summary>
    public int CpuSeconds { get; set; } = 5;

    /// <summary>Memory limit (MiB).</summary>
    public int MemoryMiB { get; set; } = 256;

    /// <summary>Max process/thread count (PID limit).</summary>
    public int PidLimit { get; set; } = 64;

    /// <summary>Writable <c>/tmp</c> size (MiB); rootfs stays read-only.</summary>
    public int TmpSizeMiB { get; set; } = 32;

    /// <summary>Cap on captured stdout/stderr (bytes), to bound the response.</summary>
    public int MaxOutputBytes { get; set; } = 64 * 1024;
}
