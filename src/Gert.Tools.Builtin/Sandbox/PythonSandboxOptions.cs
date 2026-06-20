using Gert.Tools.Sandbox.GVisor;
using Gert.Tools.Sandbox.Monty;
namespace Gert.Tools.Sandbox;

/// <summary>
/// The <c>run_python</c> sandbox backend, behind the one
/// <see cref="Gert.Tools.IPythonSandbox"/> port (configuration.md section 4;
/// chat-and-tools.md section sandbox; security F5). The uniform "Type -> Parameters" shape:
/// <see cref="Type"/> picks <c>Monty</c> (Pydantic's Rust Python interpreter sidecar - default,
/// no container infra; <see cref="MontyParameters"/>) or <c>GVisor</c> (the <c>runsc</c>
/// container; <see cref="GVisorParameters"/>); the per-backend knobs bind from
/// <c>Gert:Tools:Sandbox:Parameters</c>. The fields here are the cross-backend per-run caps both
/// implementations read; their defaults are the security posture (hard mem/wall caps, bounded
/// output). All non-secret.
/// </summary>
public sealed class PythonSandboxOptions
{
    public const string SectionName = "Gert:Tools:Sandbox";

    /// <summary>
    /// Which backend <see cref="Gert.Tools.IPythonSandbox"/> resolves to:
    /// <c>Monty</c> (default - the Rust Python interpreter sidecar, no container infra) or
    /// <c>GVisor</c> (the <c>runsc</c> container). Case-insensitive; an unknown value fails
    /// fast at startup.
    /// </summary>
    public string Type { get; set; } = "Monty";

    /// <summary>
    /// Wall-clock kill timeout for a run (seconds). With the monty backend this must sit
    /// <b>below</b> <see cref="MontyParameters.RequestTimeoutSeconds"/> (its HTTP backstop) -
    /// enforced fail-fast at startup.
    /// </summary>
    public int WallClockSeconds { get; set; } = 10;

    /// <summary>Memory limit (MiB).</summary>
    public int MemoryMiB { get; set; } = 256;

    /// <summary>Cap on captured stdout/stderr (bytes), to bound the response.</summary>
    public int MaxOutputBytes { get; set; } = 64 * 1024;
}
