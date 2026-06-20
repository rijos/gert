using Gert.Tools;

namespace Gert.Service.Chat;

/// <summary>
/// The chat loop's <see cref="IToolHost"/> (chat-and-tools.md section tool host): pre-scoped to the
/// active conversation's object store and carrying the turn's <see cref="ToolLimits.Deadline"/>.
/// The RAG resource, <see cref="Ui"/>, and <see cref="Delegate"/> are wired in later phases - until
/// then Rag throws (any premature use fails loudly), Ui is null (autonomous), and Delegate is a no-op.
/// </summary>
internal sealed class ChatToolHost : IToolHost
{
    public ChatToolHost(IObjectResource objects, DateTimeOffset? deadline)
    {
        ArgumentNullException.ThrowIfNull(objects);
        Resources = new ChatResources(objects);
        Limits = new ToolLimits(deadline, null);
    }

    public IToolResources Resources { get; }

    public IToolUi? Ui => null;

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
