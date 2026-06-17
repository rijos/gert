using Gert.Tools.Sandbox.GVisor;
using Gert.Tools.Sandbox.Monty;
namespace Gert.Tools.Sandbox;

/// <summary>
/// The <c>run_python</c> sandbox backend (<c>Gert:Tools:Sandbox</c>): pick an implementation
/// via <see cref="Type"/>, configure it under <c>Parameters</c> - the uniform
/// "functionality -> Type -> Parameters" shape (configuration.md section 4; chat-and-tools.md
/// section sandbox; security F5). Two backends sit behind the one
/// <see cref="Gert.Service.External.IPythonSandbox"/> port: <c>Monty</c> (Pydantic's Rust
/// Python interpreter via a sidecar - the default, no container infra; its connection is
/// <see cref="MontyParameters"/>) and <c>GVisor</c> (the <c>runsc</c> container; its
/// per-backend knobs are <see cref="GVisorParameters"/>). Case-insensitive; an unknown
/// <see cref="Type"/> fails fast at startup.
///
/// <para>
/// The fields below are the <b>cross-backend</b> per-run caps both implementations read; the
/// backend-specific connection/knobs live under <c>Gert:Tools:Sandbox:Parameters</c> (bound to
/// <see cref="MontyParameters"/> or <see cref="GVisorParameters"/> per <see cref="Type"/>). All
/// non-secret. The defaults are the security posture: hard mem/wall caps, bounded output.
/// </para>
/// </summary>
public sealed class PythonSandboxOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Gert:Tools:Sandbox";

    /// <summary>
    /// Which backend <see cref="Gert.Service.External.IPythonSandbox"/> resolves to:
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
