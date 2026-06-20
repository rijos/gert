namespace Gert.Tools;

/// <summary>
/// A batch of prompts put to the user in one interaction (chat-and-tools.md section ask the user) -
/// the answer carries one entry per prompt, in this order.
/// </summary>
public sealed record InteractionRequest(IReadOnlyList<InteractionPrompt> Prompts);
