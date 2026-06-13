using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gert.Model.Json;

/// <summary>
/// The single JSON wire contract for the whole API surface (rest-api.md): snake_case
/// property names and <b>string</b> enums (<c>"ready"</c>, <c>"assistant"</c>, ...) so
/// every consumer reads one shape - no per-field casing/enum guesswork. Applied to
/// the MVC pipeline, the SSE writer, the API test deserialisers, and the persisted
/// <c>turn_events</c> payloads from this one place. Lives in Gert.Model because it is
/// the contract OF the model types - both the Api transports and the Service's turn
/// runner serialize with it.
/// </summary>
public static class GertJsonOptions
{
    /// <summary>Apply the contract (naming policy + string-enum converter) to an options bag.</summary>
    public static void Configure(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        options.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
    }

    /// <summary>A ready-made options instance (web defaults + the contract) for ad-hoc (de)serialisation.</summary>
    public static readonly JsonSerializerOptions Default = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Configure(options);
        return options;
    }
}
