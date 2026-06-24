namespace Gert.Validation;

/// <summary>One validation failure - the offending member and a message.</summary>
public sealed record ValidationError
{
    /// <summary>Dotted member path that failed, e.g. <c>Params.Temperature</c>.</summary>
    public required string Property { get; init; }

    public required string Message { get; init; }

    public string? Code { get; init; }
}
