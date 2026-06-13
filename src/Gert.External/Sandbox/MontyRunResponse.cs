using System.Text.Json.Serialization;

namespace Gert.External.Sandbox;

/// <summary>Wire response from the monty sidecar's <c>/run</c> (snake_case JSON).</summary>
public sealed record MontyRunResponse
{
    /// <summary>Captured printed output.</summary>
    [JsonPropertyName("stdout")]
    public string Stdout { get; init; } = string.Empty;

    /// <summary>Captured error text (a monty error's rendered traceback/message).</summary>
    [JsonPropertyName("stderr")]
    public string Stderr { get; init; } = string.Empty;

    /// <summary>0 on success; non-zero when the run errored or was killed.</summary>
    [JsonPropertyName("exit_code")]
    public int ExitCode { get; init; }

    /// <summary>True if the run hit monty's wall-clock limit.</summary>
    [JsonPropertyName("timed_out")]
    public bool TimedOut { get; init; }
}
