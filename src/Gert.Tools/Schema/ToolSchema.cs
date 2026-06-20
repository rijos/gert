using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace Gert.Tools.Schema;

/// <summary>
/// Generates a typed tool's JSON-schema <c>ParametersSchema</c> from its <c>TArgs</c> record
/// (chat-and-tools.md section "tool specs are a token budget") so the model-facing schema is
/// derived from the one source of truth - the record's shape, nullability, and
/// <see cref="ToolParameterDescriptionAttribute"/>/<see cref="ToolParameterEnumAttribute"/>/
/// <see cref="ToolParameterRangeAttribute"/>/<see cref="ToolParameterItemsAttribute"/>
/// annotations - rather than a hand-written string that can silently drift from the record.
/// <para>
/// Output is COMPACT JSON (no insignificant whitespace) to spend the fewest tokens; property
/// names are snake_case via <see cref="JsonNamingPolicy.SnakeCaseLower"/> so they match the
/// wire contract (GertJsonOptions). A property is required IFF non-nullable. Cached per type.
/// </para>
/// <para>
/// Lives in the contracts assembly (Gert.Model-only) and uses only the BCL
/// (System.Text.Json + reflection), so the service layer can reference it without an adapter.
/// </para>
/// </summary>
public static class ToolSchema
{
    private static readonly ConcurrentDictionary<Type, string> Cache = new();

    /// <summary>Generate (and cache) the parameter schema for <typeparamref name="TArgs"/>.</summary>
    public static string Generate<TArgs>() => Generate(typeof(TArgs));

    /// <summary>Generate (and cache) the parameter schema for <paramref name="argsType"/>.</summary>
    public static string Generate(Type argsType)
    {
        ArgumentNullException.ThrowIfNull(argsType);
        return Cache.GetOrAdd(argsType, Build);
    }

    private static string Build(Type argsType)
    {
        var buffer = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteObjectSchema(writer, argsType);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    // {"type":"object","properties":{...}[,"required":[...]]} - the "required" array is
    // OMITTED entirely when no property is required (declaration metadata order throughout).
    private static void WriteObjectSchema(Utf8JsonWriter writer, Type type)
    {
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var nullability = new NullabilityInfoContext();
        var required = new List<string>();

        writer.WriteStartObject();
        writer.WriteString("type", "object");

        writer.WriteStartObject("properties");
        foreach (var prop in props)
        {
            var wireName = JsonNamingPolicy.SnakeCaseLower.ConvertName(prop.Name);
            writer.WritePropertyName(wireName);
            WriteProperty(writer, prop);

            if (!IsOptional(prop, nullability))
            {
                required.Add(wireName);
            }
        }

        writer.WriteEndObject();

        if (required.Count > 0)
        {
            writer.WriteStartArray("required");
            foreach (var name in required)
            {
                writer.WriteStringValue(name);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static void WriteProperty(Utf8JsonWriter writer, PropertyInfo prop)
    {
        writer.WriteStartObject();

        var (jsonType, elementType) = MapType(prop.PropertyType);
        writer.WriteString("type", jsonType);

        if (prop.GetCustomAttribute<ToolParameterDescriptionAttribute>() is { } desc)
        {
            writer.WriteString("description", desc.Description);
        }

        if (prop.GetCustomAttribute<ToolParameterEnumAttribute>() is { } @enum)
        {
            writer.WriteStartArray("enum");
            foreach (var value in @enum.Values)
            {
                writer.WriteStringValue(value);
            }

            writer.WriteEndArray();
        }

        if (prop.GetCustomAttribute<ToolParameterRangeAttribute>() is { } range)
        {
            writer.WriteNumber("minimum", range.Minimum);
            writer.WriteNumber("maximum", range.Maximum);
        }

        if (jsonType == "array")
        {
            writer.WritePropertyName("items");
            WriteItemsSchema(writer, elementType!);
        }

        if (prop.GetCustomAttribute<ToolParameterItemsAttribute>() is { } items)
        {
            writer.WriteNumber("minItems", items.MinItems);
            writer.WriteNumber("maxItems", items.MaxItems);
        }

        // A nested-record property expands inline: "properties" + its own "required"
        // (arrays-of-records take the WriteItemsSchema path instead).
        if (jsonType == "object")
        {
            WriteNestedObjectMembers(writer, prop.PropertyType);
        }

        writer.WriteEndObject();
    }

    // The schema of an array element T: a nested record -> a full object schema (recurse),
    // int -> {"type":"integer"}, string -> {"type":"string"}, etc.
    private static void WriteItemsSchema(Utf8JsonWriter writer, Type elementType)
    {
        var (jsonType, _) = MapType(elementType);
        if (jsonType == "object")
        {
            WriteObjectSchema(writer, elementType);
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("type", jsonType);
        writer.WriteEndObject();
    }

    // For a property whose type is a nested record: write its "properties" + "required"
    // into the already-open object (which already carries "type":"object").
    private static void WriteNestedObjectMembers(Utf8JsonWriter writer, Type type)
    {
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var nullability = new NullabilityInfoContext();
        var required = new List<string>();

        writer.WriteStartObject("properties");
        foreach (var prop in props)
        {
            var wireName = JsonNamingPolicy.SnakeCaseLower.ConvertName(prop.Name);
            writer.WritePropertyName(wireName);
            WriteProperty(writer, prop);

            if (!IsOptional(prop, nullability))
            {
                required.Add(wireName);
            }
        }

        writer.WriteEndObject();

        if (required.Count > 0)
        {
            writer.WriteStartArray("required");
            foreach (var name in required)
            {
                writer.WriteStringValue(name);
            }

            writer.WriteEndArray();
        }
    }

    // Maps a CLR type to its (jsonType, elementType) - elementType is non-null only for arrays.
    private static (string JsonType, Type? ElementType) MapType(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying == typeof(string))
        {
            return ("string", null);
        }

        if (underlying == typeof(int))
        {
            return ("integer", null);
        }

        if (underlying == typeof(bool))
        {
            return ("boolean", null);
        }

        if (TryGetEnumerableElement(underlying) is { } element)
        {
            return ("array", element);
        }

        // Anything else is treated as a nested record -> object.
        return ("object", null);
    }

    // The element type of T[]/IReadOnlyList<T>/IEnumerable<T>/etc, or null if not a
    // collection. string is NOT treated as a collection (it maps to "string" first).
    private static Type? TryGetEnumerableElement(Type type)
    {
        if (type == typeof(string))
        {
            return null;
        }

        if (type.IsArray)
        {
            return type.GetElementType();
        }

        if (type.IsGenericType && IsEnumerableInterface(type))
        {
            return type.GetGenericArguments()[0];
        }

        var iface = type.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        if (iface is not null)
        {
            return iface.GetGenericArguments()[0];
        }

        return typeof(IEnumerable).IsAssignableFrom(type) ? typeof(object) : null;
    }

    private static bool IsEnumerableInterface(Type type) =>
        type.GetGenericTypeDefinition() == typeof(IEnumerable<>);

    // Optional IFF nullable: a value type via Nullable.GetUnderlyingType (int? -> optional),
    // a reference type via NullabilityInfoContext (ReadState == Nullable -> optional).
    private static bool IsOptional(PropertyInfo prop, NullabilityInfoContext nullability)
    {
        var type = prop.PropertyType;
        if (type.IsValueType)
        {
            return Nullable.GetUnderlyingType(type) is not null;
        }

        return nullability.Create(prop).ReadState == NullabilityState.Nullable;
    }
}
