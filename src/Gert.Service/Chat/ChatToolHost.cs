using Gert.Tools;

namespace Gert.Service.Chat;

/// <summary>
/// The chat loop's <see cref="IToolHost"/> (chat-and-tools.md section tool host): pre-scoped to the
/// active conversation's object store, carrying the turn's <see cref="ToolLimits.Deadline"/>, and
/// (for an interactive turn) a <see cref="ChatToolUi"/> wired to the question registry. The RAG
/// resource and <see cref="Delegate"/> are wired in later phases - until then Rag throws (any
/// premature use fails loudly) and Delegate is a no-op.
/// </summary>
internal sealed class ChatToolHost : IToolHost
{
    public ChatToolHost(IObjectResource objects, IToolUi? ui, DateTimeOffset? deadline)
    {
        ArgumentNullException.ThrowIfNull(objects);
        Resources = new ChatResources(objects);
        Ui = ui;
        Limits = new ToolLimits(deadline, null);
    }

    public IToolResources Resources { get; }

    public IToolUi? Ui { get; }

    public IToolDelegate Delegate { get; } = new NoOpDelegate();

    public ToolLimits Limits { get; }

    private sealed class ChatResources(IObjectResource objects) : IToolResources
    {
        public IObjectResource Objects { get; } = objects;

        public IRagResource Rag =>
            throw new NotSupportedException("The Rag resource is wired in a later phase.");
    }

    private sealed class NoOpDelegate : IToolDelegate;
}
