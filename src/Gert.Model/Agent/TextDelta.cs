namespace Gert.Model.Agent;

/// <summary>A chunk of answer text (raw, uncoalesced).</summary>
public sealed record TextDelta(string Text) : AgentEvent;
