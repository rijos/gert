using Gert.Agent.Loop;
using Gert.Tools;
using Gert.Tools.Hosting;
using Gert.Tools.Resources;
using Gert.Tools.Ui;

namespace Gert.Agent.Hosting;

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
        IDocumentResource documents,
        IToolUi? ui,
        IToolDelegate @delegate,
        DateTimeOffset? deadline)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(rag);
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(@delegate);
        Resources = new ChatResources(objects, rag, documents);
        Ui = ui;
        Delegate = @delegate;
        Limits = new ToolLimits(deadline, null);
    }

    public IToolResources Resources { get; }

    public IToolUi? Ui { get; }

    public IToolDelegate Delegate { get; }

    /// <summary>
    /// The turn host carries no card: the loop's per-call <c>BudgetedToolHost</c> binds the real
    /// <see cref="ToolCardCollector"/> the tool reports to, so a tool only ever sees that per-call
    /// card. (A direct host call - e.g. a sub-agent's autonomous host - discards side-effects.)
    /// </summary>
    public IToolCard Card => NullToolCard.Instance;

    public ToolLimits Limits { get; }

    private sealed class ChatResources(IObjectResource objects, IRagResource rag, IDocumentResource documents)
        : IToolResources
    {
        public IObjectResource Objects { get; } = objects;

        public IRagResource Rag { get; } = rag;

        public IDocumentResource Documents { get; } = documents;
    }
}
