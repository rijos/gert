namespace Gert.Tools.Sandbox.GVisor;

/// <summary>
/// Pure builder for the <c>runsc</c> invocation that runs one ephemeral Python sandbox
/// (chat-and-tools.md section sandbox; security F5). Assembling the args + limits here with no
/// process spawn is what makes the security posture unit-testable (egress off, read-only rootfs,
/// no <c>/data</c> mount, CPU/mem/PID/wall caps present): CI asserts the produced argument list,
/// while the real <c>runsc</c> exec is availability-gated to hosts with gVisor.
/// </summary>
public static class SandboxCommandBuilder
{
    /// <summary>
    /// Build the full <c>runsc run</c> argument vector for a one-shot Python container.
    /// The <paramref name="bundlePath"/> is the prepared OCI bundle dir (rootfs +
    /// config.json with the code as the entrypoint); this method assembles the
    /// security-relevant flags that wrap it.
    /// </summary>
    public static IReadOnlyList<string> BuildRunscArgs(
        GVisorParameters gvisor, string containerId, string bundlePath)
    {
        ArgumentNullException.ThrowIfNull(gvisor);
        ArgumentException.ThrowIfNullOrEmpty(containerId);
        ArgumentException.ThrowIfNullOrEmpty(bundlePath);

        var args = new List<string>
        {
            // No host networking namespace passthrough; gVisor's own netstack.
            "--network", gvisor.EgressEnabled ? "host" : "none",
            // Read-only rootfs overlay - writes go to the tmpfs overlay, discarded on exit.
            "--overlay2", "none",
            // Drop raw sockets etc.; rely on the OCI config for the rest of the caps.
            "run",
            "--detach=false",
            "--bundle", bundlePath,
            containerId,
        };

        return args;
    }

    /// <summary>
    /// Build the OCI runtime <c>config.json</c> security knobs as a flat map the bundle
    /// writer applies. This is where the per-run limits live; exposed as data so a test
    /// can assert the posture without parsing a process arg string.
    /// </summary>
    public static SandboxRuntimeConfig BuildRuntimeConfig(
        PythonSandboxOptions options, GVisorParameters gvisor, string code)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(gvisor);
        ArgumentNullException.ThrowIfNull(code);

        return new SandboxRuntimeConfig
        {
            ReadOnlyRootfs = true,
            // No /data mount - the sandbox must never see user DB files (F5).
            MountsDataVolume = false,
            // Outbound off by default (F5).
            NetworkEnabled = gvisor.EgressEnabled,
            // A small writable /tmp only.
            TmpfsSizeMiB = gvisor.TmpSizeMiB,
            MemoryLimitMiB = options.MemoryMiB,
            CpuLimitSeconds = gvisor.CpuSeconds,
            PidLimit = gvisor.PidLimit,
            WallClockSeconds = options.WallClockSeconds,
            // Run as an unprivileged uid with no ambient capabilities.
            RunAsUid = 65534,
            DropAllCapabilities = true,
            NoNewPrivileges = true,
            // The Python program to execute (passed to the runtime as the entrypoint stdin).
            Code = code,
        };
    }
}
