using Gert.Model.Chat;

namespace Gert.Tools.Hosting;

/// <summary>
/// The discard <see cref="IToolCard"/>: every report is a no-op. The default card on a host that
/// emits nothing (the per-turn <c>ChatToolHost</c> before a per-call card is bound, and the
/// autonomous sub-agent/headless host where a delegated tool's side-effects must vanish).
/// </summary>
public sealed class NullToolCard : IToolCard
{
    /// <summary>The shared instance.</summary>
    public static readonly NullToolCard Instance = new();

    private NullToolCard()
    {
    }

    /// <inheritdoc />
    public void ReportCitations(IReadOnlyList<Citation> citations)
    {
    }

    /// <inheritdoc />
    public void ReportArtifact(Artifact artifact)
    {
    }

    /// <inheritdoc />
    public void ReportStdout(string stdout)
    {
    }

    /// <inheritdoc />
    public void ReportTodos(IReadOnlyList<TodoItem> todos)
    {
    }
}
