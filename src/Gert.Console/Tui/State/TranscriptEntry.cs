using System.Text;
using Gert.Model;

namespace Gert.Console.Tui.State;

/// <summary>
/// One message in the transcript model — the mutable aggregate the streamed
/// <see cref="Gert.Model.Events.ChatEvent"/>s build up (the analog of the
/// SPA's reactive message object in <c>state/chat.js</c>).
/// </summary>
public sealed class TranscriptEntry
{
    public required MessageRole Role { get; init; }

    /// <summary>Streamed body text (delta events append here).</summary>
    public StringBuilder Text { get; } = new();

    /// <summary>Streamed thinking text (reasoning events append here).</summary>
    public StringBuilder Reasoning { get; } = new();

    /// <summary>Tool cards in call order.</summary>
    public List<ToolCardModel> Tools { get; } = [];

    /// <summary>Citation footnotes in arrival order.</summary>
    public List<CitationNote> Citations { get; } = [];

    /// <summary>Errors appended by <c>error</c> events.</summary>
    public StringBuilder Errors { get; } = new();

    public bool Streaming { get; set; }

    public bool Cancelled { get; set; }

    public int? TokenCount { get; set; }

    public long? DurationMs { get; set; }
}
