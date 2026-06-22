using Gert.Model.Agent;

namespace Gert.Agent;

/// <summary>
/// One running agent: its background identity (<see cref="Number"/>), the event stream the caller
/// reads while it's busy, and a <see cref="Completion"/> that ran-to-end / faulted / cancelled. The
/// final <see cref="AgentResult"/> rides <see cref="Completion"/> (and the trailing
/// <see cref="TurnFinished"/> event); a loop fault surfaces by awaiting <see cref="Completion"/>.
/// </summary>
public interface IAgentRun
{
    /// <summary>The agent number - this run's background identity.</summary>
    int Number { get; }

    /// <summary>"While it's busy, I get stuff back": the live event stream, completing when the run ends.</summary>
    IAsyncEnumerable<AgentEvent> Events { get; }

    /// <summary>Ran-to-end (the result) / faulted (rethrows) / cancelled (OCE).</summary>
    Task<AgentResult> Completion { get; }
}
