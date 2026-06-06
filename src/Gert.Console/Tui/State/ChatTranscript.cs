using System.Globalization;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Model.Events;

namespace Gert.Console.Tui.State;

/// <summary>
/// The headless transcript model (U16) — the console analog of the SPA's
/// <c>state/chat.js</c> + <c>services/chat.js apply()</c>: streamed
/// <see cref="ChatEvent"/>s mutate entries, and <see cref="Lines"/> projects
/// them into the flat <see cref="RenderLine"/> list the view draws. No
/// Terminal.Gui types anywhere — tests drive event sequences and assert lines.
/// </summary>
public sealed class ChatTranscript
{
    private readonly List<TranscriptEntry> _entries = [];
    private readonly Dictionary<string, bool> _regionOverrides = new(StringComparer.Ordinal);
    private IReadOnlyList<RenderLine>? _lines;

    /// <summary>Raised after every mutation — the view redraws on it.</summary>
    public event Action? Changed;

    /// <summary>The entries, oldest first.</summary>
    public IReadOnlyList<TranscriptEntry> Entries => _entries;

    /// <summary>The latest context/speed numbers (message_end), for the status bar.</summary>
    public ContextUsage? Usage { get; private set; }

    /// <summary>The model's context window (from the catalog) — feeds <see cref="Usage"/>.</summary>
    public int? ContextCapacity { get; set; }

    /// <summary>True while an assistant turn is streaming.</summary>
    public bool Streaming => _entries.Count > 0 && _entries[^1].Streaming;

    /// <summary>Append the user's message (the send path does this before streaming).</summary>
    public void AddUser(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var entry = new TranscriptEntry { Role = MessageRole.User };
        entry.Text.Append(content);
        _entries.Add(entry);
        Touch();
    }

    /// <summary>Open the assistant bubble the streamed events will fill.</summary>
    public TranscriptEntry BeginAssistant()
    {
        var entry = new TranscriptEntry { Role = MessageRole.Assistant, Streaming = true };
        _entries.Add(entry);
        Touch();
        return entry;
    }

    /// <summary>Drop everything (new chat / conversation switch).</summary>
    public void Clear()
    {
        _entries.Clear();
        _regionOverrides.Clear();
        Usage = null;
        Touch();
    }

    /// <summary>
    /// Rebuild from a persisted thread (conversation switch): messages with
    /// their tool calls and citations, ready for a possible streaming resume.
    /// </summary>
    public void Rebuild(ConversationThread thread)
    {
        ArgumentNullException.ThrowIfNull(thread);

        _entries.Clear();
        _regionOverrides.Clear();
        Usage = null;

        foreach (var message in thread.Messages)
        {
            if (message.Role is not (MessageRole.User or MessageRole.Assistant))
            {
                continue;
            }

            var entry = new TranscriptEntry { Role = message.Role };
            entry.TokenCount = message.TokenCount;
            entry.DurationMs = message.DurationMs;
            entry.Streaming = message.Role == MessageRole.Assistant
                && message.Status == MessageStatus.Streaming;

            // A still-streaming row stays EMPTY: the resume replay carries every
            // delta of the turn from the row's seq, rebuilding the bubble from
            // scratch (mirrors the SPA's resume()).
            if (!entry.Streaming)
            {
                entry.Text.Append(message.Content);
                if (message.Reasoning is { Length: > 0 } reasoning)
                {
                    entry.Reasoning.Append(reasoning);
                }
            }

            if (!entry.Streaming)
            {
                foreach (var call in thread.ToolCalls.Where(c => c.MessageId == message.Id))
                {
                    entry.Tools.Add(new ToolCardModel
                    {
                        Id = call.Id,
                        Kind = call.Kind,
                        Status = call.Status,
                        LatencyMs = call.LatencyMs,
                    });
                }

                foreach (var citation in thread.Citations.Where(c => c.MessageId == message.Id))
                {
                    entry.Citations.Add(new CitationNote(citation.Ordinal, citation.Label, citation.Locator));
                }
            }

            if (message.Role == MessageRole.Assistant && message.ContextTokens is { } ctx)
            {
                Usage = new ContextUsage
                {
                    Used = ctx,
                    Capacity = ContextCapacity,
                    LastTokenCount = message.TokenCount,
                    LastDurationMs = message.DurationMs,
                };
            }

            _entries.Add(entry);
        }

        Touch();
    }

    /// <summary>Apply one streamed event — the SPA's <c>apply()</c> switch.</summary>
    public void Apply(ChatEvent chatEvent)
    {
        ArgumentNullException.ThrowIfNull(chatEvent);

        var entry = CurrentAssistant();
        switch (chatEvent)
        {
            case MessageStartEvent:
                break;

            case DeltaEvent delta:
                entry.Text.Append(delta.Text);
                break;

            case ReasoningEvent reasoning:
                entry.Reasoning.Append(reasoning.Text);
                break;

            case ToolCallEvent call:
                entry.Tools.Add(new ToolCardModel
                {
                    Id = call.Id,
                    Kind = call.Kind,
                    Status = call.Status,
                    Summary = Summarize(call.Request),
                });
                break;

            case ToolResultEvent result:
                var card = entry.Tools.FirstOrDefault(t => string.Equals(t.Id, result.Id, StringComparison.Ordinal));
                if (card is not null)
                {
                    card.Status = result.Status;
                    card.LatencyMs = result.LatencyMs;
                    card.Hits = result.Hits;
                    card.Stdout = result.Stdout;
                    card.Todos = result.Todos;
                }

                break;

            case CitationEvent citation:
                entry.Citations.Add(new CitationNote(citation.Ordinal, citation.Label, citation.Locator));
                break;

            case ArtifactEvent artifact:
                // No canvas in the console — surface it as a meta note.
                entry.Text.Append(CultureInfo.InvariantCulture, $"\n[artifact: {artifact.Name}]");
                break;

            case MessageEndEvent end:
                entry.Streaming = false;
                entry.TokenCount = end.TokenCount;
                entry.DurationMs = end.DurationMs;
                if (end.ContextTokens is { } ctx)
                {
                    Usage = new ContextUsage
                    {
                        Used = ctx,
                        Capacity = ContextCapacity,
                        LastTokenCount = end.TokenCount,
                        LastDurationMs = end.DurationMs,
                    };
                }

                break;

            case CancelledEvent cancelled:
                entry.Streaming = false;
                entry.Cancelled = true;
                entry.TokenCount = cancelled.TokenCount;
                break;

            case ErrorEvent error:
                entry.Streaming = false;
                entry.Errors.AppendLine(error.Message);
                break;

            default:
                return;
        }

        Touch();
    }

    /// <summary>Toggle a collapsible region (Enter on its header line).</summary>
    public void ToggleRegion(string regionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(regionId);
        _regionOverrides[regionId] = !IsExpanded(regionId);
        Touch();
    }

    /// <summary>The flat render model — cached until the next mutation.</summary>
    public IReadOnlyList<RenderLine> Lines() => _lines ??= Project();

    private TranscriptEntry CurrentAssistant()
    {
        if (_entries.Count == 0 || _entries[^1].Role != MessageRole.Assistant)
        {
            return BeginAssistant();
        }

        return _entries[^1];
    }

    /// <summary>Thinking expands while streaming; tool bodies start collapsed.</summary>
    private bool IsExpanded(string regionId) =>
        _regionOverrides.TryGetValue(regionId, out var expanded)
            ? expanded
            : regionId.StartsWith("think:", StringComparison.Ordinal)
                && _entries.Count > 0
                && regionId.Equals($"think:{_entries.Count - 1}", StringComparison.Ordinal)
                && _entries[^1].Streaming;

    private void Touch()
    {
        _lines = null;
        Changed?.Invoke();
    }

    private IReadOnlyList<RenderLine> Project()
    {
        var lines = new List<RenderLine>();
        for (var i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            if (lines.Count > 0)
            {
                lines.Add(new RenderLine { Text = string.Empty, Kind = LineKind.Blank });
            }

            if (entry.Role == MessageRole.User)
            {
                lines.Add(new RenderLine { Text = "❯ You", Kind = LineKind.UserHeader });
                foreach (var (text, _) in MarkdownLite.Classify(entry.Text.ToString()))
                {
                    lines.Add(new RenderLine { Text = text, Kind = LineKind.Body });
                }

                continue;
            }

            lines.Add(new RenderLine { Text = "● Gert", Kind = LineKind.AssistantHeader });
            ProjectThinking(lines, entry, i);
            ProjectTools(lines, entry);
            ProjectBody(lines, entry);
            ProjectFootnotes(lines, entry);
        }

        return lines;
    }

    private void ProjectThinking(List<RenderLine> lines, TranscriptEntry entry, int index)
    {
        if (entry.Reasoning.Length == 0)
        {
            return;
        }

        var regionId = $"think:{index}";
        var expanded = IsExpanded(regionId);
        var thinkingLines = entry.Reasoning.ToString().Split('\n');
        var live = entry.Streaming ? " …" : string.Empty;
        lines.Add(new RenderLine
        {
            Text = $"{(expanded ? '▾' : '▸')} Thinking ({thinkingLines.Length} lines){live}",
            Kind = LineKind.ThinkingHeader,
            RegionId = regionId,
            IsRegionHeader = true,
        });

        if (!expanded)
        {
            return;
        }

        foreach (var line in thinkingLines)
        {
            lines.Add(new RenderLine
            {
                Text = "  " + line,
                Kind = LineKind.Thinking,
                RegionId = regionId,
            });
        }
    }

    private void ProjectTools(List<RenderLine> lines, TranscriptEntry entry)
    {
        foreach (var tool in entry.Tools)
        {
            var regionId = $"tool:{tool.Id}";
            var expanded = IsExpanded(regionId);
            var icon = tool.Status switch
            {
                ToolCallStatus.Running => "◌",
                ToolCallStatus.Done => "●",
                _ => "✗",
            };
            var latency = tool.LatencyMs is { } ms
                ? string.Create(CultureInfo.InvariantCulture, $" · {ms}ms")
                : string.Empty;
            var summary = tool.Summary is { Length: > 0 } s ? $" — {s}" : string.Empty;
            lines.Add(new RenderLine
            {
                Text = $"{(expanded ? '▾' : '▸')} {icon} {tool.Kind}{latency}{summary}",
                Kind = tool.Status == ToolCallStatus.Error ? LineKind.Error : LineKind.ToolHeader,
                RegionId = regionId,
                IsRegionHeader = true,
            });

            if (!expanded)
            {
                continue;
            }

            foreach (var hit in tool.Hits ?? [])
            {
                var label = hit.Title ?? hit.Doc ?? hit.Url ?? "(hit)";
                var locator = hit.Page ?? hit.Url;
                var score = hit.Score is { } sc
                    ? string.Create(CultureInfo.InvariantCulture, $"  {sc:0.00}")
                    : string.Empty;
                lines.Add(new RenderLine
                {
                    Text = $"  {label}{(locator is null ? string.Empty : $" · {locator}")}{score}",
                    Kind = LineKind.ToolBody,
                    RegionId = regionId,
                });
            }

            foreach (var line in (tool.Stdout ?? string.Empty).Split('\n'))
            {
                if (line.Length > 0 || tool.Stdout is { Length: > 0 })
                {
                    lines.Add(new RenderLine
                    {
                        Text = "  │ " + line,
                        Kind = LineKind.ToolBody,
                        RegionId = regionId,
                    });
                }
            }

            foreach (var todo in tool.Todos ?? [])
            {
                var box = todo.Status switch
                {
                    TodoStatus.Done => "[x]",
                    TodoStatus.Active => "[›]",
                    _ => "[ ]",
                };
                lines.Add(new RenderLine
                {
                    Text = $"  {box} {todo.Text}",
                    Kind = LineKind.ToolBody,
                    RegionId = regionId,
                });
            }
        }
    }

    private static void ProjectBody(List<RenderLine> lines, TranscriptEntry entry)
    {
        var body = entry.Text.ToString();
        if (body.Length > 0)
        {
            var bodyLines = MarkdownLite.Classify(body).ToList();
            for (var j = 0; j < bodyLines.Count; j++)
            {
                var (text, kind) = bodyLines[j];
                if (entry.Streaming && j == bodyLines.Count - 1)
                {
                    text += "▌";
                }

                lines.Add(new RenderLine { Text = text, Kind = kind });
            }
        }
        else if (entry.Streaming)
        {
            lines.Add(new RenderLine { Text = "▌", Kind = LineKind.Body });
        }

        if (entry.Errors.Length > 0)
        {
            foreach (var line in entry.Errors.ToString().TrimEnd('\n').Split('\n'))
            {
                lines.Add(new RenderLine { Text = line, Kind = LineKind.Error });
            }
        }

        if (entry.Cancelled)
        {
            lines.Add(new RenderLine { Text = "Stopped", Kind = LineKind.Meta });
        }
    }

    private static void ProjectFootnotes(List<RenderLine> lines, TranscriptEntry entry)
    {
        foreach (var citation in entry.Citations)
        {
            var locator = citation.Locator is { Length: > 0 } l ? $" — {l}" : string.Empty;
            lines.Add(new RenderLine
            {
                Text = $"[{citation.Ordinal}] {citation.Label}{locator}",
                Kind = LineKind.Citation,
            });
        }

        if (!entry.Streaming && entry.TokenCount is { } tokens)
        {
            var speed = entry.DurationMs is > 0
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $" · {tokens / (entry.DurationMs.Value / 1000.0):0} tok/s")
                : string.Empty;
            lines.Add(new RenderLine { Text = $"{tokens} tok{speed}", Kind = LineKind.Meta });
        }
    }

    private static string? Summarize(IReadOnlyDictionary<string, object?>? request)
    {
        if (request is null)
        {
            return null;
        }

        foreach (var key in (string[])["query", "command", "pattern", "path", "code", "timezone"])
        {
            if (request.TryGetValue(key, out var value) && value?.ToString() is { Length: > 0 } text)
            {
                return text.Length > 80 ? text[..80] + "…" : text;
            }
        }

        return null;
    }
}
