namespace Gert.External.Sandbox;

/// <summary>
/// Pure, side-effect-free builder for the <c>runsc</c> invocation that runs one
/// ephemeral Python sandbox (chat-and-tools.md § sandbox; security F5). Keeping the
/// argument + limit assembly here — with no process spawn — is what makes the security
/// posture unit-testable: assert that egress is off, the rootfs is read-only, there is
/// no <c>/data</c> mount, and the CPU/mem/PID/wall caps are present.
///
/// <para>
/// <b>Integration-only:</b> the actual <c>runsc</c> exec is guarded by availability
/// detection and only runs on a host with gVisor. CI tests the produced argument list,
/// not a live container.
/// </para>
/// </summary>
public static class SandboxCommandBuilder
{
    /// <summary>
    /// Build the full <c>runsc run</c> argument vector for a one-shot Python container.
    /// The <paramref name="bundlePath"/> is the prepared OCI bundle dir (rootfs +
    /// config.json with the code as the entrypoint); this method assembles the
    /// security-relevant flags that wrap it.
    /// </summary>
    public static IReadOnlyList<string> BuildRunscArgs(SandboxOptions options, string containerId, string bundlePath)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(containerId);
        ArgumentException.ThrowIfNullOrEmpty(bundlePath);

        var args = new List<string>
        {
            // No host networking namespace passthrough; gVisor's own netstack.
            "--network", options.EgressEnabled ? "host" : "none",
            // Read-only rootfs overlay — writes go to the tmpfs overlay, discarded on exit.
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
    public static SandboxRuntimeConfig BuildRuntimeConfig(SandboxOptions options, string code)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(code);

        return new SandboxRuntimeConfig
        {
            ReadOnlyRootfs = true,
            // No /data mount — the sandbox must never see user DB files (F5).
            MountsDataVolume = false,
            // Outbound off by default (F5).
            NetworkEnabled = options.EgressEnabled,
            // A small writable /tmp only.
            TmpfsSizeMiB = options.TmpSizeMiB,
            MemoryLimitMiB = options.MemoryMiB,
            CpuLimitSeconds = options.CpuSeconds,
            PidLimit = options.PidLimit,
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
