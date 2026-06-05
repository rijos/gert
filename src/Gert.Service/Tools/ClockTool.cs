using System.Globalization;
using System.Text.Json;

namespace Gert.Service.Tools;

/// <summary>
/// The clock tool. Model function <c>get_datetime</c>: returns the current date
/// and time — UTC always, plus the wall-clock in an optional IANA timezone —
/// because the model has no clock of its own. Reads time only through the
/// injected <see cref="TimeProvider"/>, so tests pin the instant and the result
/// is fully deterministic. No citations, no external world.
/// </summary>
public sealed class ClockTool : ITool
{
    private readonly TimeProvider _time;

    public ClockTool(TimeProvider time)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <inheritdoc />
    public string Id => "clock";

    /// <inheritdoc />
    public string Name => "get_datetime";

    /// <inheritdoc />
    public string Description =>
        "Get the current date and time (UTC, plus an optional IANA timezone like "
        + "'Europe/Amsterdam'). Use whenever the user asks about now, today, or dates.";

    /// <inheritdoc />
    public string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "timezone": { "type": "string", "description": "Optional IANA timezone id (e.g. 'Europe/Amsterdam'). Defaults to UTC." }
          }
        }
        """;

    /// <inheritdoc />
    public Task<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        return Task.FromResult(Execute(invocation));
    }

    private ToolResult Execute(ToolInvocation invocation)
    {
        string? timezone;
        try
        {
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            timezone = doc.RootElement.TryGetProperty("timezone", out var tz) ? tz.GetString() : null;
        }
        catch (JsonException ex)
        {
            return new ToolResult { Success = false, Error = $"invalid arguments: {ex.Message}" };
        }

        var utc = _time.GetUtcNow();

        var zoneId = string.IsNullOrWhiteSpace(timezone) ? "UTC" : timezone.Trim();
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // A bad zone is a tool error the model can read and correct, never a
            // torn-down turn.
            return new ToolResult { Success = false, Error = $"unknown timezone '{zoneId}'" };
        }

        var local = TimeZoneInfo.ConvertTime(utc, zone);

        var resultJson = JsonSerializer.Serialize(new
        {
            utc = utc.ToString("O", CultureInfo.InvariantCulture),
            local = local.ToString("O", CultureInfo.InvariantCulture),
            timezone = zone.Id,
            unix = utc.ToUnixTimeSeconds(),
            day_of_week = local.DayOfWeek.ToString(),
        });

        return new ToolResult
        {
            Success = true,
            ResultJson = resultJson,
            // The human-facing reading the card renders verbatim.
            Stdout = $"{local:yyyy-MM-dd HH:mm:ss} ({zone.Id}, {local.DayOfWeek})",
        };
    }
}
