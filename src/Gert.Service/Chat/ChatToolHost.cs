using Gert.Tools;
using Gert.Tools.Hosting;
using Gert.Tools.Resources;
using Gert.Tools.Ui;

namespace Gert.Service.Chat;

/// <summary>
/// The chat loop's <see cref="IToolHost"/> (chat-and-tools.md section tool host): pre-scoped to the
/// active conversation's object store + the project's RAG index, carrying the turn's
/// <see cref="ToolLimits.Deadline"/>, a <see cref="ChatToolUi"/> wired to the question registry (for
/// an interactive turn; null for the autonomous sub-agent host), and an <see cref="IToolDelegate"/>
/// over the same <see cref="IAgentLoop"/> the turn runs (a no-op on the sub-agent's own nested host,
/// so delegation never recurses).
/// </summary>
internal sealed class ChatToolHost : IToolHost
{
    public ChatToolHost(
        IObjectResource objects,
        IRagResource rag,
        IToolUi? ui,
        IToolDelegate @delegate,
        DateTimeOffset? deadline)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(rag);
        ArgumentNullException.ThrowIfNull(@delegate);
        Resources = new ChatResources(objects, rag);
        Ui = ui;
        Delegate = @delegate;
        Limits = new ToolLimits(deadline, null);
    }

    public IToolResources Resources { get; }

    public IToolUi? Ui { get; }

    public IToolDelegate Delegate { get; }

    public ToolLimits Limits { get; }

    private sealed class ChatResources(IObjectResource objects, IRagResource rag) : IToolResources
    {
        public IObjectResource Objects { get; } = objects;

        public IRagResource Rag { get; } = rag;
    }
}
