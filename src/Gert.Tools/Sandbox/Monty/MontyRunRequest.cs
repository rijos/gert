using System.Text.Json.Serialization;

namespace Gert.Tools.Sandbox.Monty;

/// <summary>
/// Wire request to the monty sidecar's <c>/run</c> (snake_case JSON). Carries the code
/// plus the shared per-run limits from <see cref="PythonSandboxOptions"/>; the sidecar maps
/// these onto monty's <c>ResourceLimits</c>.
/// </summary>
public sealed record MontyRunRequest
{
    /// <summary>The Python source to execute.</summary>
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    /// <summary>Wall-clock kill for the run (seconds) -> monty <c>max_duration_secs</c>.</summary>
    [JsonPropertyName("wall_clock_seconds")]
    public int WallClockSeconds { get; init; }

    /// <summary>Memory ceiling (MiB) -> monty <c>max_memory</c>.</summary>
    [JsonPropertyName("memory_mib")]
    public int MemoryMiB { get; init; }

    /// <summary>Cap on captured stdout/stderr (bytes), to bound the response.</summary>
    [JsonPropertyName("max_output_bytes")]
    public int MaxOutputBytes { get; init; }
}
