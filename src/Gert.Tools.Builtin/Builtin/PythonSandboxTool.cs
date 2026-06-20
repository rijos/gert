using Gert.Model;
using Gert.Model.Tools;
using Gert.Tools;
using Gert.Tools.Args;
using Gert.Tools.Hosting;
using Gert.Tools.Ports;
using Gert.Tools.Results;
using Gert.Validation;

namespace Gert.Tools.Builtin;

/// <summary>
/// The sandbox tool (chat-and-tools.md section sandbox). Model function
/// <c>run_python</c>: runs the supplied code in an ephemeral gVisor container via
/// <see cref="IPythonSandbox"/> (egress off, no <c>/data</c> mount, hard limits - all
/// behind the port) and returns captured stdout/stderr + exit status in a
/// <see cref="PythonSandboxToolResult"/>. The sandbox produces no citations.
/// <para>
/// Failure handling: a non-zero exit or a timeout returns a FAILED
/// <see cref="ToolCallResult{TResult}"/> that still carries the captured payload
/// (the base maps a value present on a failure), so the model sees the failure and
/// can recover. A hard throw (e.g. <c>runsc</c> failing to spawn) is caught and
/// surfaced as a plain error, so the tool never tears down the turn.
/// </para>
/// </summary>
public sealed class PythonSandboxTool : ToolCall<PythonSandboxArgs, PythonSandboxToolResult>
{
    private readonly IPythonSandbox _sandbox;

    public PythonSandboxTool(IValidationProvider validation, IPythonSandbox sandbox)
        : base(validation)
    {
        _sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));
    }

    /// <inheritdoc />
    public override string Id => "sandbox";

    /// <inheritdoc />
    public override string Name => "run_python";

    /// <inheritdoc />
    public override string Title => "Run Python";

    /// <inheritdoc />
    public override string Icon => "file";

    /// <inheritdoc />
    public override string Group => "standard";

    /// <inheritdoc />
    public override string Description =>
        "Run Python in an isolated, network-less sandbox and return its stdout. "
        + "Use it whenever an exact computed result is needed (arithmetic, dates, "
        + "data transforms) - never do non-trivial math yourself or answer with "
        + "unexecuted code.";

    /// <inheritdoc />
    public override async Task<ToolCallResult<PythonSandboxToolResult>> CallAsync(
        PythonSandboxArgs args,
        ToolInvocation invocation,
        IToolHost host,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(invocation);

        PythonSandboxResult result;
        try
        {
            result = await _sandbox.RunPythonAsync(args.Code, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Hard failure (e.g. the container failed to start): degrade to a tool
            // error the model can read, never a torn-down turn.
            return ToolCallResult<PythonSandboxToolResult>.Fail($"sandbox error: {ex.Message}");
        }

        var payload = new PythonSandboxToolResult
        {
            ExitCode = result.ExitCode,
            Stdout = result.Stdout,
            Stderr = result.Stderr,
            TimedOut = result.TimedOut,
        };

        // A timeout / non-zero exit is a FAILED call that still ships the payload (the
        // base maps a value present on failure) so the model reads the exit + stderr.
        if (result.TimedOut)
        {
            return new ToolCallResult<PythonSandboxToolResult>
            {
                Success = false,
                Value = payload,
                Error = "the sandbox run timed out",
            };
        }

        if (result.ExitCode != 0)
        {
            return new ToolCallResult<PythonSandboxToolResult>
            {
                Success = false,
                Value = payload,
                Error = string.IsNullOrEmpty(result.Stderr)
                    ? $"the sandbox exited with code {result.ExitCode}"
                    : result.Stderr,
            };
        }

        // The card's pre block renders Stdout verbatim (the model reads ResultJson).
        return ToolCallResult<PythonSandboxToolResult>.Ok(payload, stdout: result.Stdout);
    }
}
