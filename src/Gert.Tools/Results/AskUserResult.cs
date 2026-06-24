using System.Text.Json.Serialization;

namespace Gert.Tools.Results;

/// <summary>
/// The model-facing payload of the ask-user tool. Two mutually exclusive shapes the model
/// continues from: answered (<c>{ "answered": true, "answers": [...] }</c>) pairs each prompt with
/// its reply, or a graceful no-response (<c>{ "answered": false, "reason": "timeout" }</c>). The
/// unused arm is omitted from the wire (the model never sees a null field).
/// </summary>
public sealed record AskUserResult
{
    /// <summary>Whether the user answered (false on a timeout the model continues from).</summary>
    public required bool Answered { get; init; }

    /// <summary>Why there was no answer (<c>timeout</c>); omitted when <see cref="Answered"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }

    /// <summary>Each prompt paired with its answer, in order; omitted on a no-response.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<AskUserAnswer>? Answers { get; init; }
}
