namespace Gert.Tools;

/// <summary>
/// A batch of prompts put to the user in one interaction (chat-and-tools.md section ask the user) -
/// the answer carries one entry per prompt, in this order. <see cref="CorrelationId"/> is the
/// originating tool-call id: the Ui folds its question events onto that card (the host is per-turn
/// now, so the id rides the request rather than the Ui's ctor).
/// </summary>
public sealed record InteractionRequest(string CorrelationId, IReadOnlyList<InteractionPrompt> Prompts);
