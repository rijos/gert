using System.Globalization;
using Gert.Tools;
using Gert.Validation;

namespace Gert.Tools.Builtin;

/// <summary>
/// The clock tool. Model function <c>get_datetime</c>: returns the current date
/// and time - UTC always, plus the wall-clock in an optional IANA timezone -
/// because the model has no clock of its own. Reads time only through the
/// injected <see cref="TimeProvider"/>, so tests pin the instant and the result
/// is fully deterministic. No citations, no external world.
/// </summary>
public sealed class ClockTool : ToolCall<ClockArgs, ClockResult>
{
    private readonly TimeProvider _time;

    public ClockTool(IValidationProvider validation, TimeProvider time)
        : base(validation)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <inheritdoc />
    public override string Id => "clock";

    /// <inheritdoc />
    public override string Name => "get_datetime";

    /// <inheritdoc />
    public override string Description =>
        "Get the current date and time in the user's local timezone (or an "
        + "explicit IANA timezone). Use for questions about now, today, or dates.";

    /// <inheritdoc />
    public override string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "timezone": { "type": "string", "description": "Optional IANA timezone id (e.g. 'Europe/Amsterdam'). Defaults to the user's local timezone." }
          }
        }
        """;

    /// <inheritdoc />
    public override Task<ToolCallResult<ClockResult>> CallAsync(
        ClockArgs args,
        ToolInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(invocation);

        var utc = _time.GetUtcNow();

        // Explicit argument wins; otherwise the send request's browser-supplied
        // zone makes "what time is it" user-local; UTC is the last resort.
        var zoneId = !string.IsNullOrWhiteSpace(args.Timezone)
            ? args.Timezone.Trim()
            : !string.IsNullOrWhiteSpace(invocation.ClientTimezone)
                ? invocation.ClientTimezone.Trim()
                : "UTC";
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // A bad zone is a tool error the model can read and correct, never a
            // torn-down turn.
            return Task.FromResult(ToolCallResult<ClockResult>.Fail($"unknown timezone '{zoneId}'"));
        }

        var local = TimeZoneInfo.ConvertTime(utc, zone);

        var payload = new ClockResult
        {
            Utc = utc.ToString("O", CultureInfo.InvariantCulture),
            Local = local.ToString("O", CultureInfo.InvariantCulture),
            Timezone = zone.Id,
            Unix = utc.ToUnixTimeSeconds(),
            DayOfWeek = local.DayOfWeek.ToString(),
        };

        // The human-facing reading the card renders verbatim.
        return Task.FromResult(ToolCallResult<ClockResult>.Ok(
            payload, stdout: $"{local:yyyy-MM-dd HH:mm:ss} ({zone.Id}, {local.DayOfWeek})"));
    }
}
