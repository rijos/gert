namespace Gert.Tools.Results;

/// <summary>
/// The model-facing payload of the clock tool: the instant in UTC and the resolved
/// local zone. Serialized snake_case - <c>day_of_week</c> - so the model has the
/// weekday without re-deriving it; the card's <c>Stdout</c> carries the human line.
/// </summary>
public sealed record ClockResult
{
    public required string Utc { get; init; }

    public required string Local { get; init; }

    public required string Timezone { get; init; }

    public required long Unix { get; init; }

    public required string DayOfWeek { get; init; }
}
