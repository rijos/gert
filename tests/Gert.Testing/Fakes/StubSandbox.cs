using Gert.Service.External;

namespace Gert.Testing.Fakes;

/// <summary>
/// gVisor double (testing.md §4.1): returns scripted stdout/exit without
/// launching a container. The default is a benign success; the
/// <see cref="ThatThrows"/> / <see cref="ThatFails"/> factories cover the
/// sandbox-failure path the orchestrator must handle gracefully.
/// </summary>
public sealed class StubSandbox : ISandbox
{
    private readonly SandboxResult? _result;
    private readonly Exception? _throw;

    /// <summary>A sandbox that returns the given scripted result.</summary>
    public StubSandbox(SandboxResult result)
    {
        _result = result ?? throw new ArgumentNullException(nameof(result));
    }

    private StubSandbox(Exception toThrow)
    {
        _throw = toThrow;
    }

    /// <summary>Default: exit 0 with a fixed stdout line.</summary>
    public StubSandbox()
        : this(new SandboxResult { ExitCode = 0, Stdout = "ok\n" })
    {
    }

    /// <summary>A sandbox that throws on run — the hard-failure path (e.g. runsc spawn failed).</summary>
    public static StubSandbox ThatThrows(Exception? exception = null) =>
        new(exception ?? new InvalidOperationException("sandbox failed to start"));

    /// <summary>A sandbox whose run completes but with a non-zero exit and stderr.</summary>
    public static StubSandbox ThatFails(string stderr = "Traceback (most recent call last): ...", int exitCode = 1) =>
        new(new SandboxResult { ExitCode = exitCode, Stderr = stderr });

    /// <summary>A sandbox whose run hits the wall-clock timeout.</summary>
    public static StubSandbox ThatTimesOut() =>
        new(new SandboxResult { ExitCode = -1, TimedOut = true, Stderr = "timed out" });

    /// <summary>A sandbox returning the given stdout and exit code.</summary>
    public static StubSandbox WithStdout(string stdout, int exitCode = 0) =>
        new(new SandboxResult { ExitCode = exitCode, Stdout = stdout });

    /// <inheritdoc />
    public Task<SandboxResult> RunPythonAsync(string code, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        cancellationToken.ThrowIfCancellationRequested();

        if (_throw is not null)
        {
            return Task.FromException<SandboxResult>(_throw);
        }

        return Task.FromResult(_result!);
    }
}
