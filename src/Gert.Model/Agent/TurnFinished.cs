namespace Gert.Model.Agent;

/// <summary>The loop reached its final answer - carries the accumulated content/reasoning/metrics.</summary>
public sealed record TurnFinished(AgentResult Result) : AgentEvent;
