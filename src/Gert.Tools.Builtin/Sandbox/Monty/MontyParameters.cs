namespace Gert.Tools.Builtin.Sandbox.Monty;

/// <summary>
/// Connection options for the <b>monty</b> sidecar - Pydantic's minimal Python interpreter in
/// Rust, the default <see cref="Gert.Tools.IPythonSandbox"/> backend
/// (chat-and-tools.md section sandbox). The <c>Parameters</c> bag of the sandbox functionality
/// when <c>Gert:Tools:Sandbox:Type</c> is <c>Monty</c>, bound from
/// <c>Gert:Tools:Sandbox:Parameters</c>. All non-secret.
///
/// <para>
/// The per-run security posture (wall clock, memory, output cap) lives on the cross-backend
/// <see cref="PythonSandboxOptions"/>; these options carry only how to <i>reach</i> the sidecar.
/// </para>
/// </summary>
public sealed class MontyParameters
{
    public const string SectionName = "Gert:Tools:Sandbox:Parameters";

    /// <summary>Base URL of the monty sidecar, e.g. <c>http://localhost:8077</c>.</summary>
    public string BaseUrl { get; set; } = "http://localhost:8077";

    /// <summary>
    /// Per-request HTTP timeout for a <c>/run</c> call (seconds). A backstop set
    /// <b>above</b> monty's own wall clock (<see cref="PythonSandboxOptions.WallClockSeconds"/>)
    /// so the interpreter's limit trips first and returns a clean timed-out result; the
    /// HTTP timeout only fires if the sidecar itself hangs. The relation is enforced at
    /// startup when the monty backend is selected (a validator in
    /// <c>Gert.Tools.ServiceCollectionExtensions</c> fails fast otherwise).
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 30;
}
