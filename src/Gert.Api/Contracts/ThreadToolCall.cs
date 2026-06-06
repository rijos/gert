using System.Text.Json;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Model.Events;
using Gert.Model.Json;

namespace Gert.Api.Contracts;

/// <summary>
/// A persisted tool call projected back into the SPA's card source fields — the
/// reload twin of the live <c>tool_call</c>/<c>tool_result</c> events, so a
/// thread GET reproduces the same cards the stream drew: request fields
/// (query/code) parsed from <c>request_json</c>, result fields (stdout/todos)
/// from <c>response_json</c>, hits rebuilt from the call's citations.
/// </summary>
public sealed record ThreadToolCall
{
    public required string Id { get; init; }

    public required string Kind { get; init; }

    public required ToolCallStatus Status { get; init; }

    /// <summary>Wall-clock latency for the card's tag ("rag · 142ms"). Wire: <c>latency_ms</c>.</summary>
    public long? LatencyMs { get; init; }

    /// <summary>The search/RAG query the card echoes.</summary>
    public string? Query { get; init; }

    /// <summary>The sandbox code the card renders.</summary>
    public string? Code { get; init; }

    /// <summary>Plain-text card output (sandbox stdout, the clock reading).</summary>
    public string? Stdout { get; init; }

    /// <summary>The todo checklist (the <c>set_todos</c> tool).</summary>
    public IReadOnlyList<TodoItem> Todos { get; init; } = [];

    /// <summary>Result rows (doc/web hits), rebuilt from the call's citations.</summary>
    public IReadOnlyList<ToolResultHit> Hits { get; init; } = [];

    /// <summary>Project a persisted <see cref="ToolCall"/> plus the citations it produced.</summary>
    public static ThreadToolCall From(ToolCall call, IReadOnlyList<Citation> citations)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(citations);

        var request = Parse(call.RequestJson);
        var response = Parse(call.ResponseJson);

        return new ThreadToolCall
        {
            Id = call.Id,
            Kind = call.Kind,
            Status = call.Status,
            LatencyMs = call.LatencyMs,
            Query = GetString(request, "query"),
            Code = GetString(request, "code"),
            Stdout = StdoutOf(call.Kind, response),
            Todos = TodosOf(response),
            Hits = ToolResultHit.FromCitations(citations),
        };
    }

    /// <summary>Tolerant parse — a corrupt/absent payload degrades to an empty card, never a 500.</summary>
    private static JsonElement? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var element = JsonSerializer.Deserialize<JsonElement>(json);
            return element.ValueKind == JsonValueKind.Object ? element : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement? element, string property) =>
        element is { } obj
        && obj.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? StdoutOf(string kind, JsonElement? response) => kind switch
    {
        // sandbox response_json carries the run verbatim: { exit_code, stdout, stderr, timed_out }
        "sandbox" => GetString(response, "stdout"),

        // the clock's human reading isn't persisted — rebuild it from the
        // response fields, matching ClockTool's live format.
        "clock" => ClockReading(response),

        _ => null,
    };

    private static string? ClockReading(JsonElement? response)
    {
        var local = GetString(response, "local");
        var timezone = GetString(response, "timezone");
        var dayOfWeek = GetString(response, "day_of_week");
        if (local is null || timezone is null || dayOfWeek is null)
        {
            return null;
        }

        return DateTimeOffset.TryParse(local, out var stamp)
            ? $"{stamp:yyyy-MM-dd HH:mm:ss} ({timezone}, {dayOfWeek})"
            : null;
    }

    private static IReadOnlyList<TodoItem> TodosOf(JsonElement? response)
    {
        if (response is not { } obj
            || !obj.TryGetProperty("todos", out var todos)
            || todos.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        try
        {
            return todos.Deserialize<List<TodoItem>>(GertJsonOptions.Default) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
