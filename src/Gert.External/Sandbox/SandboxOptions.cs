namespace Gert.External.Sandbox;

/// <summary>
/// Shared limits + backend selection for the <c>run_python</c> sandbox (chat-and-tools.md
/// § sandbox; security F5). Bound from configuration section <c>Gert:Sandbox</c>. All
/// non-secret. The defaults are the security posture: <b>egress off</b>, read-only
/// rootfs, no <c>/data</c> mount, hard mem/wall caps.
///
/// <para>
/// Two backends sit behind the one <see cref="Gert.Service.External.ISandbox"/> port,
/// chosen by <see cref="Backend"/>: <c>monty</c> (Pydantic's Rust Python interpreter via a
/// sidecar — the default, no container infra needed; reads <see cref="MontyOptions"/>) and
/// <c>gvisor</c> (the <c>runsc</c> container backend). The limit fields below are read by
/// both where they apply; <see cref="RunscPath"/>, <see cref="Image"/>,
/// <see cref="EgressEnabled"/>, <see cref="PidLimit"/>, and <see cref="TmpSizeMiB"/> are
/// gVisor-specific (monty has no processes, filesystem, or network to limit).
/// </para>
/// </summary>
public sealed class SandboxOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Gert:Sandbox";

    /// <summary>
    /// Which backend <see cref="Gert.Service.External.ISandbox"/> resolves to:
    /// <c>monty</c> (default — the Rust Python interpreter sidecar, no container infra) or
    /// <c>gvisor</c> (the <c>runsc</c> container). Case-insensitive; an unknown value fails
    /// fast at startup.
    /// </summary>
    public string Backend { get; set; } = "monty";

    /// <summary>Path to the <c>runsc</c> binary (gVisor backend only).</summary>
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
