using System.Globalization;

namespace Gert.Console.Tui.State;

/// <summary>
/// The context-ring equivalent (U16, <c>context-ring.js</c>): how much of the
/// model's window the conversation occupies, plus the last reply's
/// tokens/speed. Bands match the web: amber past 75%, red past 90%.
/// </summary>
public sealed record ContextUsage
{
    /// <summary>Tokens occupied by the last completed round (<c>message_end.ContextTokens</c>).</summary>
    public required int Used { get; init; }

    /// <summary>The model's context window, when the catalog declares one.</summary>
    public int? Capacity { get; init; }

    public int? LastTokenCount { get; init; }

    public long? LastDurationMs { get; init; }

    /// <summary>Used / Capacity, or null without a declared window.</summary>
    public double? Ratio => Capacity is > 0 ? (double)Used / Capacity.Value : null;

    /// <summary>0 = ok, 1 = amber (&gt;75%), 2 = red (&gt;90%) — matches the web ring.</summary>
    public int Band => Ratio switch
    {
        > 0.90 => 2,
        > 0.75 => 1,
        _ => 0,
    };

    /// <summary>Last-reply generation speed, when both numbers are known.</summary>
    public double? TokensPerSecond =>
        LastTokenCount is > 0 && LastDurationMs is > 0
            ? LastTokenCount.Value / (LastDurationMs.Value / 1000.0)
            : null;

    /// <summary>Status-bar text, e.g. <c>"▰▰▱▱▱ 12.4K/128K · 41 tok/s"</c>.</summary>
    public string Display()
    {
        var parts = new List<string>();
        if (Capacity is > 0)
        {
            var filled = (int)Math.Round((Ratio ?? 0) * 5, MidpointRounding.AwayFromZero);
            filled = Math.Clamp(filled, 0, 5);
            parts.Add(new string('▰', filled) + new string('▱', 5 - filled));
            parts.Add($"{Compact(Used)}/{Compact(Capacity.Value)}");
        }
        else
        {
            parts.Add($"{Compact(Used)} ctx");
        }

        if (TokensPerSecond is { } tps)
        {
            parts.Add($"{tps:0} tok/s");
        }

        return string.Join(" · ", parts);
    }

    private static string Compact(int tokens) =>
        tokens >= 1000
            ? (tokens / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "K"
            : tokens.ToString(CultureInfo.InvariantCulture);
}
