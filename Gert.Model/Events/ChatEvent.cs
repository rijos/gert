using System.Text.Json.Serialization;

namespace Gert.Model.Events;

/// <summary>
/// One event in the streamed chat response — the polymorphic, System.Text.Json
/// friendly model of the SSE event table (rest-api.md § sending a message).
/// The chat service yields these as an <c>IAsyncEnumerable&lt;ChatEvent&gt;</c>;
/// the Api renders each as an SSE <c>event:/data:</c> frame and the Console
/// prints it — transport never leaks into the service (tech-stack.md
/// § Architecture).
/// <para>
/// The <see cref="JsonDerivedTypeAttribute"/> discriminators are the SSE
/// <c>event:</c> names, so the wire <c>data:</c> payload carries a matching
/// <c>type</c> field and the union round-trips through STJ.
/// </para>
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(MessageStartEvent), "message_start")]
[JsonDerivedType(typeof(ToolCallEvent), "tool_call")]
[JsonDerivedType(typeof(ToolResultEvent), "tool_result")]
[JsonDerivedType(typeof(DeltaEvent), "delta")]
[JsonDerivedType(typeof(CitationEvent), "citation")]
[JsonDerivedType(typeof(ArtifactEvent), "artifact")]
[JsonDerivedType(typeof(MessageEndEvent), "message_end")]
[JsonDerivedType(typeof(ErrorEvent), "error")]
public abstract record ChatEvent
{
    /// <summary>The SSE <c>event:</c> name for this event (e.g. <c>delta</c>).</summary>
    [JsonIgnore]
    public abstract string EventName { get; }
}
