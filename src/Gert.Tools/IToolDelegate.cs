namespace Gert.Tools;

/// <summary>
/// Delegation to a nested agent loop (chat-and-tools.md section sub-agent) - the seam the
/// <c>run_sub_agent</c> tool drives so it depends on a contract, not the loop impl. Declared here;
/// the <c>RunAsync</c> surface is filled in Phase 6 when <c>IAgentLoop</c> is extracted and the
/// chat driver supplies a <c>ChatToolDelegate</c> over it.
/// </summary>
public interface IToolDelegate
{
}
