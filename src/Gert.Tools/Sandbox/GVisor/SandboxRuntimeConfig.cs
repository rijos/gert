namespace Gert.Tools.Sandbox.GVisor;

/// <summary>
/// The security-relevant per-run configuration assembled by
/// <see cref="SandboxCommandBuilder.BuildRuntimeConfig"/> and written into the OCI
/// bundle's <c>config.json</c> before <c>runsc run</c>. Exposed as plain data so the
/// posture (egress off, read-only rootfs, no <c>/data</c>, caps, dropped privs) is
/// unit-assertable without spawning a container.
/// </summary>
public sealed record SandboxRuntimeConfig
{
    /// <summary>Rootfs is mounted read-only (writes land on the discarded overlay).</summary>
    public required bool ReadOnlyRootfs { get; init; }

    /// <summary>Whether the user data volume is mounted - must be <c>false</c> (F5).</summary>
    public required bool MountsDataVolume { get; init; }

    /// <summary>Outbound network - off by default (F5).</summary>
    public required bool NetworkEnabled { get; init; }

    /// <summary>Size of the writable <c>/tmp</c> tmpfs (MiB).</summary>
    public required int TmpfsSizeMiB { get; init; }

    /// <summary>Memory cap (MiB).</summary>
    public required int MemoryLimitMiB { get; init; }

    /// <summary>CPU-time cap (seconds).</summary>
    public required int CpuLimitSeconds { get; init; }

    /// <summary>Max process/thread count.</summary>
    public required int PidLimit { get; init; }

    /// <summary>Wall-clock kill timeout (seconds).</summary>
    public required int WallClockSeconds { get; init; }

    /// <summary>Unprivileged uid the container process runs as.</summary>
    public required int RunAsUid { get; init; }

    /// <summary>Drop all Linux capabilities.</summary>
    public required bool DropAllCapabilities { get; init; }

    /// <summary>Set <c>no_new_privs</c> so setuid binaries can't re-escalate.</summary>
    public required bool NoNewPrivileges { get; init; }

    /// <summary>The Python source to execute.</summary>
    public required string Code { get; init; }
}
