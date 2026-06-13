using System.Text.Json;

namespace Gert.Api.WebSockets;

/// <summary>
/// The safe parser for client WS messages (rest-api.md section the ws endpoint):
/// strictly bounded, never throws on hostile input - malformed JSON, a missing
/// or unknown <c>type</c>, or wrong field shapes all return null, which the
/// socket loop ignores. The socket only ever closes on transport-level events,
/// not on message content.
/// </summary>
public static class ClientMessageParser
{
    /// <summary>Cap on a parsed range page; the client pages with the cursor.</summary>
    private const int MaxRangeLimit = 1000;

    /// <summary>Parse one text message; null when it is not a valid client message.</summary>
    public static ClientMessage? Parse(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8Json);
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeProp)
                || typeProp.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return typeProp.GetString() switch
            {
                "subscribe" => new ClientMessage.Subscribe(ReadLong(root, "after", 0)),
                "range" => new ClientMessage.Range(
                    ReadLong(root, "after", 0),
                    (int)Math.Clamp(ReadLong(root, "limit", 200), 1, MaxRangeLimit)),
                "cancel" => new ClientMessage.Cancel(),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long ReadLong(JsonElement root, string name, long fallback) =>
        root.TryGetProperty(name, out var prop)
        && prop.ValueKind == JsonValueKind.Number
        && prop.TryGetInt64(out var value)
        && value >= 0
            ? value
            : fallback;
}
