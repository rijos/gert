using System.Text.Json;
using Gert.Service.External;

namespace Gert.Service.Tools;

/// <summary>
/// The sandbox tool (chat-and-tools.md section sandbox). Model function
/// <c>run_python</c>: runs the supplied code in an ephemeral gVisor container via
/// <see cref="IPythonSandbox"/> (egress off, no <c>/data</c> mount, hard limits - all
/// behind the port) and returns captured stdout/stderr + exit status in a
/// <see cref="ToolResult"/>. The sandbox produces no citations.
/// <para>
/// Failure handling: a non-zero exit or a timeout returns a
/// <see cref="ToolResult"/> with <see cref="ToolResult.Success"/> false and the
/// captured stderr - the model sees the failure and can recover. A hard throw
/// (e.g. <c>runsc</c> failing to spawn) is caught and surfaced the same way, so
/// the tool never tears down the turn.
/// </para>
/// </summary>
public sealed class PythonSandboxTool : ITool
{
    private readonly IPythonSandbox _sandbox;

    public PythonSandboxTool(IPythonSandbox sandbox)
    {
        _sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));
    }

    /// <inheritdoc />
    public string Id => "sandbox";

    /// <inheritdoc />
    public string Name => "run_python";

    /// <inheritdoc />
    public string Description =>
        "Run Python in an isolated, network-less sandbox and return its stdout. "
        + "Use it whenever an exact computed result is needed (arithmetic, dates, "
        + "data transforms) - never do non-trivial math yourself or answer with "
        + "unexecuted code.";

    /// <inheritdoc />
    public string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "code": { "type": "string", "description": "The Python source to execute." }
          },
          "required": ["code"]
        }
        """;

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        string code;
        try
        {
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            code = doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() ?? string.Empty : string.Empty;
        }
        catch (JsonException ex)
        {
            return new ToolResult { Success = false, Error = $"invalid arguments: {ex.Message}" };
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return new ToolResult { Success = false, Error = "the 'code' argument is required" };
        }

        PythonSandboxResult result;
        try
        {
            result = await _sandbox.RunPythonAsync(code, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Hard failure (e.g. the container failed to start): degrade to a tool
            // error the model can read, never a torn-down turn.
            return new ToolResult { Success = false, Error = $"sandbox error: {ex.Message}" };
        }

        var resultJson = JsonSerializer.Serialize(new
        {
            exit_code = result.ExitCode,
            stdout = result.Stdout,
            stderr = result.Stderr,
            timed_out = result.TimedOut,
        });

        if (result.TimedOut)
        {
            return new ToolResult
            {
                Success = false,
                ResultJson = resultJson,
                Error = "the sandbox run timed out",
            };
        }

        if (result.ExitCode != 0)
        {
            return new ToolResult
            {
                Success = false,
                ResultJson = resultJson,
                Error = string.IsNullOrEmpty(result.Stderr)
                    ? $"the sandbox exited with code {result.ExitCode}"
                    : result.Stderr,
            };
        }

        return new ToolResult
        {
            Success = true,
            ResultJson = resultJson,
            // The card's pre block renders this verbatim (the model reads ResultJson).
            Stdout = result.Stdout,
        };
    }
}
