namespace Gert.Tools.Args;

/// <summary>
/// Arguments for the clock tool (<c>get_datetime</c>): an optional IANA
/// <see cref="Timezone"/>. Omitted means the request's client zone, then UTC -
/// so a null is legitimate and the validator only shape-checks a supplied id.
/// </summary>
public sealed record ClockArgs
{
    /// <summary>Optional IANA timezone id (e.g. <c>Europe/Amsterdam</c>); null defaults to the client zone.</summary>
    public string? Timezone { get; init; }
}
