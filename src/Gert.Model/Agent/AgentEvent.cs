namespace Gert.Model.Agent;

/// <summary>
/// The agent's one output: the cross-cutting vocabulary the loop and its consumers
/// both speak (refactor: split the noun). POCOs, no deps - the loop knows nothing of
/// logs, buses, conversations, HTTP, or resumption; it emits these through a single
/// sink. The consumer (the event-log tee) maps them to the persisted/wire
/// <see cref="Events.ChatEvent"/> union. Text/reasoning arrive as raw per-chunk deltas
/// (coalescing is the consumer's job); tool/round/finish events are discrete.
/// </summary>
public abstract record AgentEvent;
