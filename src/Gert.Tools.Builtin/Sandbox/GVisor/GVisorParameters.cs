namespace Gert.Tools.Builtin.Sandbox.GVisor;

/// <summary>
/// The gVisor-specific knobs for the <c>run_python</c> sandbox - the <c>Parameters</c> bag of
/// the sandbox functionality when <c>Gert:Tools:Sandbox:Type</c> is <c>GVisor</c>, bound from
/// <c>Gert:Tools:Sandbox:Parameters</c> (chat-and-tools.md section sandbox; security F5). All
/// non-secret. The defaults are the security posture: <b>egress off</b>, read-only rootfs, no
/// <c>/data</c> mount, hard CPU/PID/tmp caps. The cross-backend per-run caps (wall clock,
/// memory, output) live on <see cref="PythonSandboxOptions"/>; monty has no processes,
/// filesystem, or network to limit, so these apply to gVisor only.
/// </summary>
public sealed class GVisorParameters
{
    public const string SectionName = "Gert:Tools:Sandbox:Parameters";

    public int CpuSeconds { get; set; } = 5;

    public int PidLimit { get; set; } = 64;

    /// <summary>Writable <c>/tmp</c> size (MiB); rootfs stays read-only.</summary>
    public int TmpSizeMiB { get; set; } = 32;

    public string RunscPath { get; set; } = "runsc";

    public string Image { get; set; } = "gert-sandbox-python";

    /// <summary>
    /// Outbound network. <b>Off by default</b> - the exfiltration brake for arbitrary
    /// code (F5). An allow-list is opt-in only and never the default.
    /// </summary>
    public bool EgressEnabled { get; set; }
}
