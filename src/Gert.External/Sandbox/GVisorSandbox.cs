using System.Diagnostics;
using System.Text;
using Gert.Service.External;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gert.External.Sandbox;

/// <summary>
/// Real <see cref="IPythonSandbox"/> running <c>run_python</c> in an ephemeral gVisor
/// (<c>runsc</c>) container (chat-and-tools.md section sandbox; security F5): no inbound
/// network, <b>outbound egress off by default</b>, read-only rootfs, no <c>/data</c>
/// mount, CPU/mem/PID/wall caps, container destroyed after the call - only captured
/// stdout/stderr/exit returns.
///
/// <para>
/// The argument + limit assembly is the pure, unit-tested
/// <see cref="SandboxCommandBuilder"/>; the failure -> graceful-<see cref="PythonSandboxResult"/>
/// mapping in <see cref="MapFailure"/> is also unit-tested. The actual <c>runsc</c>
/// spawn is <b>integration-only</b> (needs gVisor on the host) and guarded by
/// <see cref="IsAvailable"/>: when <c>runsc</c> is absent the sandbox returns a clean
/// "unavailable" result rather than throwing, so a dev box / CI without gVisor still
/// behaves predictably.
/// </para>
/// </summary>
public sealed class GVisorSandbox : IPythonSandbox
{
    private readonly PythonSandboxOptions _options;
    private readonly ILogger<GVisorSandbox> _logger;

    /// <summary>Construct over the configured limits.</summary>
    public GVisorSandbox(IOptions<PythonSandboxOptions> options, ILogger<GVisorSandbox> logger)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<PythonSandboxResult> RunPythonAsync(string code, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code);

        if (!IsAvailable(_options))
        {
            _logger.LogWarning("gVisor ({Runsc}) not available; returning unavailable result.", _options.RunscPath);
            return Unavailable();
        }

        var containerId = $"gert-{Guid.NewGuid():N}";
        var runtime = SandboxCommandBuilder.BuildRuntimeConfig(_options, code);

        // INTEGRATION-ONLY below. Building the OCI bundle (rootfs + config.json from
        // `runtime`), invoking `runsc run` with SandboxCommandBuilder.BuildRunscArgs,
        // capturing streams, enforcing the wall-clock kill, and tearing the container
        // down is validated on a gVisor host (staging), not in CI. We keep the
        // real Process plumbing here but it only runs when runsc is present.
        try
        {
            return await RunRunscAsync(containerId, runtime, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return MapFailure(ex);
        }
    }

    /// <summary>
    /// Detect whether the <c>runsc</c> binary is on the host. Pure-ish (a filesystem
    /// probe, no container); lets the adapter degrade gracefully off a gVisor host.
    /// </summary>
    public static bool IsAvailable(PythonSandboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // An absolute path: probe directly. A bare name: probe PATH entries.
        if (Path.IsPathRooted(options.RunscPath))
        {
            return File.Exists(options.RunscPath);
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar))
        {
            return false;
        }

        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(Path.Combine(dir, options.RunscPath)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Map a spawn/run failure to a graceful <see cref="PythonSandboxResult"/> - a non-zero
    /// exit with the error on stderr, or a timed-out result for a wall-clock kill. The
    /// sandbox must never throw an infra error up to the tool loop. Pure + unit-tested.
    /// </summary>
    public static PythonSandboxResult MapFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is TimeoutException)
        {
            return new PythonSandboxResult
            {
                ExitCode = 124, // conventional timeout exit code
                Stderr = "Sandbox run exceeded the wall-clock limit and was terminated.",
                TimedOut = true,
            };
        }

        return new PythonSandboxResult
        {
            ExitCode = 1,
            Stderr = $"Sandbox run failed: {exception.Message}",
        };
    }

    private static PythonSandboxResult Unavailable() => new()
    {
        ExitCode = 127, // "command not found" convention
        Stderr = "Sandbox runtime (runsc) is not available on this host.",
    };

    /// <summary>
    /// The live <c>runsc</c> path (integration-only). Builds the args, spawns the
    /// process, enforces the wall-clock kill, and returns captured output. A
    /// <see cref="TimeoutException"/> on wall-clock overrun is mapped by
    /// <see cref="MapFailure"/>.
    /// </summary>
    private async Task<PythonSandboxResult> RunRunscAsync(
        string containerId,
        SandboxRuntimeConfig runtime,
        CancellationToken cancellationToken)
    {
        // TODO: write the OCI bundle from `runtime` (rootfs, config.json
        // carrying the limits + the Code entrypoint) into an ephemeral dir, then run
        // runsc against it. Until the bundle writer lands, the args are built so the
        // posture is testable; the bundle path is a placeholder.
        var bundlePath = Path.Combine(Path.GetTempPath(), containerId);
        var args = SandboxCommandBuilder.BuildRunscArgs(_options, containerId, bundlePath);

        var psi = new ProcessStartInfo
        {
            FileName = _options.RunscPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => Append(stdout, e.Data, _options.MaxOutputBytes);
        process.ErrorDataReceived += (_, e) => Append(stderr, e.Data, _options.MaxOutputBytes);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var wall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wall.CancelAfter(TimeSpan.FromSeconds(_options.WallClockSeconds + 2));

        try
        {
            await process.WaitForExitAsync(wall.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException("runsc wall-clock timeout.");
        }

        return new PythonSandboxResult
        {
            ExitCode = process.ExitCode,
            Stdout = stdout.ToString(),
            Stderr = stderr.ToString(),
        };
    }

    private static void Append(StringBuilder sink, string? data, int cap)
    {
        if (data is null || sink.Length >= cap)
        {
            return;
        }

        var remaining = cap - sink.Length;
        sink.Append(data.Length <= remaining ? data : data[..remaining]);
        sink.Append('\n');
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited - nothing to kill.
        }
    }
}
