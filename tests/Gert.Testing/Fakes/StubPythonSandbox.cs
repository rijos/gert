using Gert.Model;
using Gert.Model.Tools;
using Gert.Tools;
using Gert.Tools.Ports;

namespace Gert.Testing.Fakes;

/// <summary>
/// gVisor double (testing.md section 4.1): returns scripted stdout/exit without
/// launching a container. The default is a benign success; the
/// <see cref="ThatThrows"/> / <see cref="ThatFails"/> factories cover the
/// sandbox-failure path the orchestrator must handle gracefully.
/// </summary>
public sealed class StubPythonSandbox : IPythonSandbox
{
    private readonly PythonSandboxResult? _result;
    private readonly Exception? _throw;

    /// <summary>A sandbox that returns the given scripted result.</summary>
    public StubPythonSandbox(PythonSandboxResult result)
    {
        _result = result ?? throw new ArgumentNullException(nameof(result));
    }

    private StubPythonSandbox(Exception toThrow)
    {
        _throw = toThrow;
    }

    /// <summary>Default: exit 0 with a fixed stdout line.</summary>
    public StubPythonSandbox()
        : this(new PythonSandboxResult { ExitCode = 0, Stdout = "ok\n" })
    {
    }

    /// <summary>A sandbox that throws on run - the hard-failure path (e.g. runsc spawn failed).</summary>
    public static StubPythonSandbox ThatThrows(Exception? exception = null) =>
        new(exception ?? new InvalidOperationException("sandbox failed to start"));

    /// <summary>A sandbox whose run completes but with a non-zero exit and stderr.</summary>
    public static StubPythonSandbox ThatFails(string stderr = "Traceback (most recent call last): ...", int exitCode = 1) =>
        new(new PythonSandboxResult { ExitCode = exitCode, Stderr = stderr });

    /// <summary>A sandbox whose run hits the wall-clock timeout.</summary>
    public static StubPythonSandbox ThatTimesOut() =>
        new(new PythonSandboxResult { ExitCode = -1, TimedOut = true, Stderr = "timed out" });

    /// <summary>A sandbox returning the given stdout and exit code.</summary>
    public static StubPythonSandbox WithStdout(string stdout, int exitCode = 0) =>
        new(new PythonSandboxResult { ExitCode = exitCode, Stdout = stdout });

    /// <inheritdoc />
    public Task<PythonSandboxResult> RunPythonAsync(string code, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        cancellationToken.ThrowIfCancellationRequested();

        if (_throw is not null)
        {
            return Task.FromException<PythonSandboxResult>(_throw);
        }

        return Task.FromResult(_result!);
    }
}
