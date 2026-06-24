namespace Gert.Model.Agent;

/// <summary>
/// The live "Running" card: the model has named an entitled tool. Emitted twice for one
/// call - first while arguments still stream (<see cref="Request"/> null), then again at
/// execution time with the parsed args - so the card appears early and fills in. Same
/// <see cref="CallId"/> both times; the card updates in place. Never emitted for an
/// unentitled call.
/// </summary>
public sealed record ToolStarted(
    string CallId,
    string Kind,
    IReadOnlyDictionary<string, object?>? Request) : AgentEvent;
