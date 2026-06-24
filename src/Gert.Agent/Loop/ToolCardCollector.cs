using Gert.Model.Chat;
using Gert.Tools.Hosting;

namespace Gert.Agent.Loop;

/// <summary>
/// The per-tool-call <see cref="IToolCard"/> the loop hands a tool: it accumulates the side-effects
/// the tool reports (citations, artifacts, plain-text output, the todo list) so the loop can fold
/// them onto the call's <see cref="Gert.Model.Agent.ExecutedToolCall"/> after it returns. The driver's
/// tee then persists/renders them - the tool never touches the bus, the row, or citation binding.
/// One instance per call; not shared.
/// </summary>
internal sealed class ToolCardCollector : IToolCard
{
    private readonly List<Citation> _citations = [];
    private List<Artifact>? _artifacts;

    /// <summary>Plain-text card output the tool reported (last write wins; tools report once).</summary>
    public string? Stdout { get; private set; }

    /// <summary>The todo list the tool reported (replace-not-patch; the latest call is the truth).</summary>
    public IReadOnlyList<TodoItem>? Todos { get; private set; }

    /// <summary>Citations reported across the call, in report order (the driver binds them to the row).</summary>
    public IReadOnlyList<Citation> Citations => _citations;

    /// <summary>Artifacts the call created/updated, or null if none.</summary>
    public IReadOnlyList<Artifact>? Artifacts => _artifacts;

    /// <inheritdoc />
    public void ReportCitations(IReadOnlyList<Citation> citations)
    {
        ArgumentNullException.ThrowIfNull(citations);
        _citations.AddRange(citations);
    }

    /// <inheritdoc />
    public void ReportArtifact(Artifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        (_artifacts ??= []).Add(artifact);
    }

    /// <inheritdoc />
    public void ReportStdout(string stdout) => Stdout = stdout;

    /// <inheritdoc />
    public void ReportTodos(IReadOnlyList<TodoItem> todos) => Todos = todos;
}
