using Gert.Model.Chat;

namespace Gert.Tools.Hosting;

/// <summary>
/// The tool's OUTPUT seam on <see cref="IToolHost"/> (chat-and-tools.md section the tool loop):
/// a tool reports its UI/persistence side-effects - footnote citations, canvas artifacts, plain-text
/// card output, the todo checklist - instead of returning them in <see cref="ToolResult"/>. The
/// model-facing result (<see cref="ToolResult.ResultJson"/>) is all the model sees; everything else
/// is "pushed into the tool" and emitted here. The IMPL lives in the chat driver (<c>Gert.Agent</c>),
/// which owns persist-then-publish, the <c>tool_calls</c> row id, and citation renumber/bind - so the
/// tool decides WHAT to emit and the driver decides HOW it is persisted and ordered. Mirrors the
/// existing <see cref="Ui.IToolUi"/> ask_user seam; an autonomous driver (sub-agent, headless) wires a
/// no-op card so a delegated tool's side-effects are simply discarded.
///
/// <para>
/// Citations are reported WITHOUT a row id (the driver binds them to the row it allocates for the
/// call); artifacts the tool has already persisted via <see cref="Resources.IObjectResource"/> are
/// reported only so the live canvas opens/updates.
/// </para>
/// </summary>
public interface IToolCard
{
    /// <summary>Report footnote citations this call produced (RAG / web hits); the driver binds them to the call's row.</summary>
    void ReportCitations(IReadOnlyList<Citation> citations);

    /// <summary>Report a canvas artifact this call created/updated (already persisted via the object resource).</summary>
    void ReportArtifact(Artifact artifact);

    /// <summary>Report plain-text card output (sandbox stdout, the clock reading) - presentation only.</summary>
    void ReportStdout(string stdout);

    /// <summary>Report the todo checklist to render on the card (the set_todos tool).</summary>
    void ReportTodos(IReadOnlyList<TodoItem> todos);
}
