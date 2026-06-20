namespace Gert.Tools;

/// <summary>
/// The human-interaction surface (chat-and-tools.md section ask the user) - the generalized port
/// the <c>ask_user</c> tool drives so it depends on a contract, not the chat impl. Declared here;
/// the <c>AskAsync</c> surface (over InteractionRequest/Result) is filled in Phase 5. Null on an
/// autonomous host, where a <see cref="ITool.RequiresHuman"/> tool is excluded at advertise time
/// and fails closed at execution.
/// </summary>
public interface IToolUi
{
}
