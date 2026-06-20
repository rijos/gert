using Gert.Tools;

namespace Gert.Service.Chat;

/// <summary>
/// The transitional <see cref="IToolHost"/> the chat loop hands tools until the real chat host lands
/// (Phase 6: Chat Objects, Project Rag, ChatToolUi, ChatToolDelegate). It carries the turn's
/// <see cref="ToolLimits.Deadline"/> - all the tools currently read - and throws
/// <see cref="NotSupportedException"/> from the resource ports so any premature capability use fails
/// loudly rather than silently no-ops. <see cref="Ui"/> is null and <see cref="Delegate"/> a no-op,
/// matching their not-yet-wired state.
/// </summary>
internal sealed class NotSupportedToolHost : IToolHost
{
    public NotSupportedToolHost(DateTimeOffset? deadline) => Limits = new ToolLimits(deadline, null);

    public IToolResources Resources { get; } = new NotSupportedResources();

    public IToolUi? Ui => null;

    public IToolDelegate Delegate { get; } = new NoOpDelegate();

    public ToolLimits Limits { get; }

    private sealed class NotSupportedResources : IToolResources
    {
        public IObjectResource Objects =>
            throw new NotSupportedException("The Objects resource is wired in a later phase.");

        public IRagResource Rag =>
            throw new NotSupportedException("The Rag resource is wired in a later phase.");
    }

    private sealed class NoOpDelegate : IToolDelegate;
}
