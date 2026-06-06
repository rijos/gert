using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Gert.Service.Tools;

namespace Gert.Console.Tools;

/// <summary>
/// The TUI's gated local shell (U16). Model function <c>run_shell</c>: run a
/// command line via <c>/bin/sh -c</c> in the <see cref="WorkspaceRoot"/>
/// (build, test, git status — the things the gVisor sandbox can't see). Every
/// run is gated through <see cref="IToolApprover"/>; output is captured and
/// capped; a timeout or non-zero exit is a tool error the model can read —
/// SandboxTool's failure discipline. Console-only: the API host never
/// registers this (its execution surface is the egress-off sandbox).
/// </summary>
public sealed class ShellExecTool : ITool
{
    private const int TimeoutSeconds = 120;
    private const int MaxCapturedChars = 64 * 1024;

    private readonly WorkspaceRoot _workspace;
    private readonly IToolApprover _approver;

    public ShellExecTool(WorkspaceRoot workspace, IToolApprover approver)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _approver = approver ?? throw new ArgumentNullException(nameof(approver));
    }

    /// <inheritdoc />
    public string Id => "shell";

    /// <inheritdoc />
    public string Name => "run_shell";

    /// <inheritdoc />
    public string Description =>
        "Run a shell command in the local workspace (e.g. build, test, git). The user "
        + $"approves each run. Times out after {TimeoutSeconds}s; keep commands non-interactive.";

    /// <inheritdoc />
    public string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "command": { "type": "string", "description": "The command line to run with sh -c." }
          },
          "required": ["command"]
        }
        """;

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        string command;
        try
        {
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            command = doc.RootElement.TryGetProperty("command", out var c) ? c.GetString() ?? string.Empty : string.Empty;
        }
        catch (JsonException ex)
        {
            return new ToolResult { Success = false, Error = $"invalid arguments: {ex.Message}" };
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            return new ToolResult { Success = false, Error = "the 'command' argument is required" };
        }

        var request = new ApprovalRequest
        {
            Kind = Id,
            Path = ".",
            Command = command,
        };

        var decision = _approver.AutoApprove
            ? ApprovalDecision.Approve
            : await _approver.RequestAsync(request, cancellationToken).ConfigureAwait(false);

        if (decision == ApprovalDecision.Deny)
        {
            return new ToolResult
            {
                Success = false,
                ResultJson = JsonSerializer.Serialize(new { denied = true, command }),
                Error = "the user denied this command",
            };
        }

        try
        {
            return await RunAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Hard failure (e.g. sh missing): degrade to a tool error, never a
            // torn-down turn.
            return new ToolResult { Success = false, Error = $"shell error: {ex.Message}" };
        }
    }

    private async Task<ToolResult> RunAsync(string command, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            ArgumentList = { "-c", command },
            WorkingDirectory = _workspace.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => Append(stdout, e.Data);
        process.ErrorDataReceived += (_, e) => Append(stderr, e.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var timedOut = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited between the timeout and the kill.
            }

            // A user stop propagates (turn cancel); the tool's own deadline degrades
            // to a readable failure.
            cancellationToken.ThrowIfCancellationRequested();
            timedOut = true;
        }

        var exitCode = timedOut ? -1 : process.ExitCode;
        var resultJson = JsonSerializer.Serialize(new
        {
            exit_code = exitCode,
            stdout = stdout.ToString(),
            stderr = stderr.ToString(),
            timed_out = timedOut,
        });

        if (timedOut)
        {
            return new ToolResult
            {
                Success = false,
                ResultJson = resultJson,
                Error = $"the command timed out after {TimeoutSeconds}s",
            };
        }

        var display = stdout.Length > 0 ? stdout.ToString() : stderr.ToString();
        if (exitCode != 0)
        {
            return new ToolResult
            {
                Success = false,
                ResultJson = resultJson,
                Stdout = display.TrimEnd('\n'),
                Error = stderr.Length > 0 ? stderr.ToString().TrimEnd('\n') : $"exit code {exitCode}",
            };
        }

        return new ToolResult
        {
            Success = true,
            ResultJson = resultJson,
            Stdout = display.TrimEnd('\n'),
        };
    }

    private static void Append(StringBuilder buffer, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (buffer)
        {
            if (buffer.Length < MaxCapturedChars)
            {
                buffer.Append(line).Append('\n');
            }
        }
    }
}
