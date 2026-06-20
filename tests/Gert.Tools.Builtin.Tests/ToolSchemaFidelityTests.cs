using System.Text.Json.Nodes;
using FluentAssertions;
using Gert.Tools.Args;
using Gert.Tools.Schema;
using Xunit;

namespace Gert.Tools.Builtin.Tests;

/// <summary>
/// Locks the GENERATED typed-tool parameter schemas (ToolSchema.Generate) to the
/// known-good hand-written schemas they replaced - these are model-facing, so a generator
/// change must not silently alter a tool-calling contract (chat-and-tools.md section "tool
/// specs are a token budget"). Each expected string is the schema exactly as the tool
/// declared it before the move; comparison is by PARSED JSON (object-key order tolerated,
/// array order significant), so whitespace/key-order differences don't matter - only the
/// schema's structure + values.
/// </summary>
public sealed class ToolSchemaFidelityTests
{
    // Each tuple: the TArgs type + the verbatim schema the tool used to hand-write.
    public static TheoryData<Type, string> TypedToolSchemas() => new()
    {
        {
            typeof(RagArgs),
            """
            {
              "type": "object",
              "properties": {
                "query": { "type": "string", "description": "The natural-language search query." },
                "k": { "type": "integer", "description": "How many passages to return (1-20, default 8).", "minimum": 1, "maximum": 20 }
              },
              "required": ["query"]
            }
            """
        },
        {
            typeof(WebSearchArgs),
            """
            {
              "type": "object",
              "properties": {
                "query": { "type": "string", "description": "The web search query." }
              },
              "required": ["query"]
            }
            """
        },
        {
            typeof(WebFetchArgs),
            """
            {
              "type": "object",
              "properties": {
                "url": { "type": "string", "description": "The absolute http(s) URL to fetch." },
                "max_chars": { "type": "integer",
                               "description": "Optional cap on the returned content (default 8000, max 20000)." }
              },
              "required": ["url"]
            }
            """
        },
        {
            typeof(PythonSandboxArgs),
            """
            {
              "type": "object",
              "properties": {
                "code": { "type": "string", "description": "The Python source to execute." }
              },
              "required": ["code"]
            }
            """
        },
        {
            typeof(TodoArgs),
            """
            {
              "type": "object",
              "properties": {
                "todos": {
                  "type": "array",
                  "description": "The complete todo list, in order.",
                  "items": {
                    "type": "object",
                    "properties": {
                      "text": { "type": "string", "description": "The step, imperative and short." },
                      "status": { "type": "string", "enum": ["pending", "active", "done"] }
                    },
                    "required": ["text", "status"]
                  }
                }
              },
              "required": ["todos"]
            }
            """
        },
        {
            typeof(ClockArgs),
            """
            {
              "type": "object",
              "properties": {
                "timezone": { "type": "string", "description": "Optional IANA timezone id (e.g. 'Europe/Amsterdam'). Defaults to the user's local timezone." }
              }
            }
            """
        },
        {
            typeof(MakeArtifactArgs),
            """
            {
              "type": "object",
              "properties": {
                "name": { "type": "string", "description": "File name with extension, e.g. index.html or notes.md." },
                "format": { "type": "string", "enum": ["html", "markdown", "svg", "python", "csharp", "cpp", "javascript", "rust"] },
                "content": { "type": "string", "description": "The entire file content." }
              },
              "required": ["name", "format", "content"]
            }
            """
        },
        {
            typeof(EditArtifactArgs),
            """
            {
              "type": "object",
              "properties": {
                "name": { "type": "string", "description": "Name of the artifact to edit." },
                "old_str": { "type": "string", "description": "Exact text to find - must match a single location verbatim." },
                "new_str": { "type": "string", "description": "Replacement text (may be empty to delete the snippet)." }
              },
              "required": ["name", "old_str", "new_str"]
            }
            """
        },
        {
            typeof(ReadArtifactArgs),
            """
            {
              "type": "object",
              "properties": {
                "name": { "type": "string", "description": "Name of the artifact to read." },
                "range": {
                  "type": "array",
                  "description": "Optional [start, end] line numbers, 1-indexed; end -1 reads to the end.",
                  "items": { "type": "integer" },
                  "minItems": 2,
                  "maxItems": 2
                }
              },
              "required": ["name"]
            }
            """
        },
        {
            typeof(ListArtifactsArgs),
            """{"type":"object","properties":{}}"""
        },
    };

    [Theory]
    [MemberData(nameof(TypedToolSchemas))]
    public void Generated_schema_matches_the_known_good_hand_written_one(Type argsType, string expected)
    {
        var generated = ToolSchema.Generate(argsType);

        JsonEqual(generated, expected).Should().BeTrue(
            "the generated schema for {0} must be the same model-facing contract as before;\n  expected: {1}\n  actual:   {2}",
            argsType.Name,
            Normalize(expected),
            generated);
    }

    [Fact]
    public void Generate_caches_per_type()
    {
        // Same instance returned on the second call (ConcurrentDictionary GetOrAdd).
        ReferenceEquals(ToolSchema.Generate<RagArgs>(), ToolSchema.Generate<RagArgs>())
            .Should().BeTrue();
    }

    [Fact]
    public void Read_artifact_schema_covers_array_items_optional_range()
    {
        // ReadArtifactArgs exercises: required string, an OPTIONAL (nullable) array of
        // integers with items + minItems/maxItems.
        var schema = JsonNode.Parse(ToolSchema.Generate<ReadArtifactArgs>())!.AsObject();

        schema["type"]!.GetValue<string>().Should().Be("object");

        var range = schema["properties"]!["range"]!.AsObject();
        range["type"]!.GetValue<string>().Should().Be("array");
        range["items"]!["type"]!.GetValue<string>().Should().Be("integer");
        range["minItems"]!.GetValue<int>().Should().Be(2);
        range["maxItems"]!.GetValue<int>().Should().Be(2);

        // Only the non-nullable "name" is required; the nullable "range" is omitted.
        var required = schema["required"]!.AsArray().Select(n => n!.GetValue<string>());
        required.Should().BeEquivalentTo(["name"]);
    }

    [Fact]
    public void Make_artifact_schema_has_enum_and_all_required()
    {
        // MakeArtifactArgs exercises: an enum (no description) + every property required.
        var schema = JsonNode.Parse(ToolSchema.Generate<MakeArtifactArgs>())!.AsObject();

        var format = schema["properties"]!["format"]!.AsObject();
        format["enum"]!.AsArray().Select(n => n!.GetValue<string>())
            .Should().Equal("html", "markdown", "svg", "python", "csharp", "cpp", "javascript", "rust");
        format.ContainsKey("description").Should().BeFalse("format carries only an enum in the schema");

        schema["required"]!.AsArray().Select(n => n!.GetValue<string>())
            .Should().BeEquivalentTo(["name", "format", "content"]);
    }

    [Fact]
    public void No_required_array_is_emitted_when_nothing_is_required()
    {
        // ListArtifactsArgs has no properties; ClockArgs's only property is nullable.
        JsonNode.Parse(ToolSchema.Generate<ListArtifactsArgs>())!.AsObject()
            .ContainsKey("required").Should().BeFalse();
        JsonNode.Parse(ToolSchema.Generate<ClockArgs>())!.AsObject()
            .ContainsKey("required").Should().BeFalse();
    }

    // Deep structural equality of two JSON strings: object keys order-insensitive,
    // array elements order-sensitive (matches the spec).
    private static bool JsonEqual(string a, string b) =>
        JsonNode.DeepEquals(SortKeys(JsonNode.Parse(a)), SortKeys(JsonNode.Parse(b)));

    // Recursively re-key objects in sorted order so DeepEquals (which IS order-sensitive
    // for object keys) tolerates a different key order; arrays keep their order.
    private static JsonNode? SortKeys(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var sorted = new JsonObject();
                foreach (var kvp in obj.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    sorted[kvp.Key] = SortKeys(kvp.Value?.DeepClone());
                }

                return sorted;
            case JsonArray arr:
                var copy = new JsonArray();
                foreach (var item in arr)
                {
                    copy.Add(SortKeys(item?.DeepClone()));
                }

                return copy;
            default:
                return node?.DeepClone();
        }
    }

    private static string Normalize(string json) => SortKeys(JsonNode.Parse(json))!.ToJsonString();
}
