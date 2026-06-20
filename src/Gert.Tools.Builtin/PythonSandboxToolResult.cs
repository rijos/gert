namespace Gert.Tools.Builtin;

/// <summary>
/// The model-facing payload of the sandbox tool: the captured exit status and
/// streams. Serialized snake_case - <c>exit_code</c>, <c>timed_out</c> - and shipped
/// even on a FAILED run (a non-zero exit or timeout still carries its stderr to the
/// model), so the base maps it whenever it is present, not only on success.
/// </summary>
public sealed record PythonSandboxToolResult
{
    public required int ExitCode { get; init; }

    public required string Stdout { get; init; }

    public required string Stderr { get; init; }

    public required bool TimedOut { get; init; }
}
