namespace Gert.Model.Agent;

/// <summary>A chunk of thinking text (raw, uncoalesced).</summary>
public sealed record ReasoningDelta(string Text) : AgentEvent;
