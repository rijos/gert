namespace Gert.Model.Agent;

/// <summary>A tool round completed (its tool messages are in history) - the streaming-row progress flush beat.</summary>
public sealed record RoundCompleted(int Round, int Tokens) : AgentEvent;
