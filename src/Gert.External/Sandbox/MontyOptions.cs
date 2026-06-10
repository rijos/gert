namespace Gert.External.Sandbox;

/// <summary>
/// Connection options for the <b>monty</b> sidecar — Pydantic's minimal Python
/// interpreter in Rust, the default <see cref="Gert.Service.External.ISandbox"/> backend
/// (chat-and-tools.md § sandbox). Bound from configuration section
/// <c>Gert:Sandbox:Monty</c>. All non-secret.
///
/// <para>
/// The per-run security posture (wall clock, memory, output cap) lives on the shared
/// <see cref="SandboxOptions"/> so both backends read one set of limits; these options
/// carry only how to <i>reach</i> the sidecar.
/// </para>
/// </summary>
public sealed class MontyOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Gert:Sandbox:Monty";

    /// <summary>Base URL of the monty sidecar, e.g. <c>http://localhost:8077</c>.</summary>
    public string BaseUrl { get; set; } = "http://localhost:8077";

    /// <summary>
    /// Per-request HTTP timeout for a <c>/run</c> call (seconds). A backstop set
    /// <b>above</b> monty's own wall clock (<see cref="SandboxOptions.WallClockSeconds"/>)
    /// so the interpreter's limit trips first and returns a clean timed-out result; the
    /// HTTP timeout only fires if the sidecar itself hangs.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 30;
}
