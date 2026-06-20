namespace Gert.Tools;

/// <summary>
/// An opt-in mixin a <see cref="ITool"/> implements when its accepted state should
/// be revived into the NEXT turn's prompt (chat-and-tools.md section cross-turn revival).
/// The planner rebuilds history as role+content only, so state a tool set via a
/// tool call in an earlier turn has already vanished from the prompt; a tool that
/// implements this re-injects it as a <c>&lt;system-reminder&gt;</c> appended at the
/// prompt TAIL - never the system prompt (its bytes must stay stable for the vLLM
/// prefix cache) and never the persisted user row (UI truth).
///
/// Pure and I/O-free by contract: the planner owns the database read and the tail
/// placement; the tool only transforms its OWN latest accepted result JSON into a
/// reminder, deciding whether one is warranted at all. A tool with nothing to
/// revive (no snapshot, nothing outstanding) returns null and costs the prompt
/// nothing. The implementation must not throw - but the planner also guards the
/// call so a parse bug can never fail the turn.
/// </summary>
public interface IToolReminder
{
    /// <summary>
    /// Build the tail reminder for this tool from <paramref name="latestResultJson"/>
    /// - the <c>ResponseJson</c> of the conversation's newest accepted call of this
    /// tool (the same echo shape the tool returns from <c>ExecuteAsync</c>), or null
    /// when there is no prior accepted call. Return the reminder text to append, or
    /// null to append nothing.
    /// </summary>
    string? BuildTailReminder(string? latestResultJson);
}
