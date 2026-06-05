namespace Gert.Service.External;

/// <summary>The outcome of a sandbox run — captured streams and exit status.</summary>
public sealed record SandboxResult
{
    public required int ExitCode { get; init; }

    public string Stdout { get; init; } = string.Empty;

    public string Stderr { get; init; } = string.Empty;

    /// <summary>True if the run hit the wall-clock timeout.</summary>
    public bool TimedOut { get; init; }
}
