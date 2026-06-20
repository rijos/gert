using FluentAssertions;
using Gert.Tools;
using Gert.Tools.Ports;
using Gert.Tools.Sandbox;
using Gert.Tools.Sandbox.GVisor;
using Xunit;

namespace Gert.Tools.Builtin.Tests;

/// <summary>
/// Unit tests for the pure sandbox command/limit builders + the failure -> graceful
/// result mapping. The security posture (egress off, read-only rootfs, no /data, caps,
/// dropped privs) is asserted on the produced config - no real runsc.
/// </summary>
public sealed class SandboxCommandBuilderTests
{
    [Fact]
    public void BuildRuntimeConfig_DefaultPosture_IsLockedDown()
    {
        var config = SandboxCommandBuilder.BuildRuntimeConfig(
            new PythonSandboxOptions(), new GVisorParameters(), "print(1)");

        config.NetworkEnabled.Should().BeFalse("egress is off by default (F5)");
        config.ReadOnlyRootfs.Should().BeTrue();
        config.MountsDataVolume.Should().BeFalse("the sandbox must never see /data (F5)");
        config.DropAllCapabilities.Should().BeTrue();
        config.NoNewPrivileges.Should().BeTrue();
        config.RunAsUid.Should().NotBe(0, "must not run as root");
        config.MemoryLimitMiB.Should().BeGreaterThan(0);
        config.CpuLimitSeconds.Should().BeGreaterThan(0);
        config.PidLimit.Should().BeGreaterThan(0);
        config.WallClockSeconds.Should().BeGreaterThan(0);
        config.Code.Should().Be("print(1)");
    }

    [Fact]
    public void BuildRuntimeConfig_EgressOptIn_FlowsThrough()
    {
        var config = SandboxCommandBuilder.BuildRuntimeConfig(
            new PythonSandboxOptions(), new GVisorParameters { EgressEnabled = true }, "x");
        config.NetworkEnabled.Should().BeTrue();
    }

    [Fact]
    public void BuildRunscArgs_DefaultUsesNoNetwork()
    {
        var args = SandboxCommandBuilder.BuildRunscArgs(new GVisorParameters(), "cid", "/tmp/bundle");

        args.Should().ContainInConsecutiveOrder("--network", "none");
        args.Should().Contain("run");
        args.Should().ContainInConsecutiveOrder("--bundle", "/tmp/bundle");
        args.Should().Contain("cid");
    }

    [Fact]
    public void BuildRunscArgs_EgressOn_UsesHostNetwork()
    {
        var args = SandboxCommandBuilder.BuildRunscArgs(
            new GVisorParameters { EgressEnabled = true }, "cid", "/tmp/b");
        args.Should().ContainInConsecutiveOrder("--network", "host");
    }

    [Fact]
    public void MapFailure_Timeout_ReturnsTimedOutResult()
    {
        var result = GVisorSandbox.MapFailure(new TimeoutException());

        result.TimedOut.Should().BeTrue();
        result.ExitCode.Should().Be(124);
        result.Stderr.Should().Contain("wall-clock");
    }

    [Fact]
    public void MapFailure_GenericError_ReturnsNonZeroExit()
    {
        var result = GVisorSandbox.MapFailure(new InvalidOperationException("boom"));

        result.TimedOut.Should().BeFalse();
        result.ExitCode.Should().Be(1);
        result.Stderr.Should().Contain("boom");
    }

    [Fact]
    public void IsAvailable_MissingBinary_ReturnsFalse()
    {
        var parameters = new GVisorParameters { RunscPath = "/nonexistent/runsc-xyz" };
        GVisorSandbox.IsAvailable(parameters).Should().BeFalse();
    }

    [Fact]
    public async Task RunPythonAsync_WhenUnavailable_ReturnsCleanResult_NotThrow()
    {
        var sandbox = new GVisorSandbox(
            Microsoft.Extensions.Options.Options.Create(new PythonSandboxOptions()),
            Microsoft.Extensions.Options.Options.Create(new GVisorParameters { RunscPath = "/nonexistent/runsc-xyz" }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GVisorSandbox>.Instance);

        var result = await sandbox.RunPythonAsync("print(1)");

        result.Should().NotBeNull();
        result.ExitCode.Should().Be(127);
        result.Stderr.Should().Contain("not available");
    }

    // Keep IPythonSandbox referenced so the test asserts against the port contract.
    [Fact]
    public void GVisorSandbox_ImplementsPort()
    {
        typeof(IPythonSandbox).IsAssignableFrom(typeof(GVisorSandbox)).Should().BeTrue();
    }
}
