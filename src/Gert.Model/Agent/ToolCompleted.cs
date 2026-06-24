namespace Gert.Model.Agent;

/// <summary>An entitled tool call finished - carries the result card payload, citations, and artifacts.</summary>
public sealed record ToolCompleted(ExecutedToolCall Call) : AgentEvent;
