using System.Threading.Channels;
using Gert.Model.Agent;

namespace Gert.Agent;

/// <summary><see cref="IAgentRun"/> over the channel the loop's background task writes into.</summary>
internal sealed class AgentRun(int number, ChannelReader<AgentEvent> reader, Task<AgentResult> completion)
    : IAgentRun
{
    public int Number => number;

    public IAsyncEnumerable<AgentEvent> Events => reader.ReadAllAsync();

    public Task<AgentResult> Completion => completion;
}
