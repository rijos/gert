namespace Gert.Tools.Args;

/// <summary>Arguments for the sandbox tool (<c>run_python</c>): the Python source to execute.</summary>
public sealed record PythonSandboxArgs
{
    /// <summary>The Python source to execute (required).</summary>
    public string Code { get; init; } = string.Empty;
}
