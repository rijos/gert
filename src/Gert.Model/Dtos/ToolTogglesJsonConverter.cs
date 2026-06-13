using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gert.Model.Dtos;

/// <summary>
/// Serializes <see cref="ToolToggles"/> as a flat JSON object of
/// <c>tool id -> bool</c> (e.g. <c>{"rag":true,"search":false}</c>) - the
/// <c>tools_json</c> wire/storage shape (storage-and-data.md section chat.db). Keeping
/// the map flat (rather than nesting under a property) means the document is
/// self-describing and adding a tool never changes the schema.
/// </summary>
public sealed class ToolTogglesJsonConverter : JsonConverter<ToolToggles>
{
    /// <inheritdoc />
    public override ToolToggles Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return new ToolToggles();
        }

        var map = JsonSerializer.Deserialize<Dictionary<string, bool>>(ref reader, options)
                  ?? new Dictionary<string, bool>(StringComparer.Ordinal);
        return new ToolToggles(map);
    }

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer,
        ToolToggles value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();
        foreach (var (id, enabled) in value.Toggles)
        {
            writer.WriteBoolean(id, enabled);
        }

        writer.WriteEndObject();
    }
}
