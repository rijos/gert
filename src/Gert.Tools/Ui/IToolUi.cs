namespace Gert.Tools.Ui;

/// <summary>
/// The human-interaction surface (chat-and-tools.md section ask the user) - the generalized port
/// the <c>ask_user</c> tool drives so it depends on a contract, not the chat impl. The chat loop's
/// <c>ChatToolUi</c> wires it to the <c>ITurnQuestions</c> registry + the question wire events; an
/// autonomous host (sub-agent, headless) has no Ui, where a <see cref="ITool.RequiresHuman"/> tool
/// is excluded at advertise time and fails closed at execution.
/// </summary>
public interface IToolUi
{
    /// <summary>
    /// Put the prompts to the user and wait for their answers. Returns
    /// <see cref="InteractionResult.Answered"/>=true with one answer per prompt on success,
    /// Answered=false with no answers on timeout (the graceful "no response" path), or
    /// <see cref="InteractionResult.Error"/> set (Answered=false) for a model-correctable
    /// rejection. A turn cancel/shutdown unwinds with <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<InteractionResult> AskAsync(InteractionRequest request, CancellationToken cancellationToken = default);
}
