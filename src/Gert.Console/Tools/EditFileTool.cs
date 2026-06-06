using System.Text.Json;
using Gert.Service.Tools;

namespace Gert.Console.Tools;

/// <summary>
/// The TUI's gated string-replace editor (U16). Model function
/// <c>edit_file</c>: replace an exact <c>old_string</c> with
/// <c>new_string</c> in one workspace file — unique-match required unless
/// <c>replace_all</c>. Gated through <see cref="IToolApprover"/> with the
/// unified diff; SandboxTool failure shapes. Console-only.
/// </summary>
public sealed class EditFileTool : ITool
{
    private readonly WorkspaceRoot _workspace;
    private readonly IToolApprover _approver;
    private readonly IWorkspaceObserver _observer;

    public EditFileTool(WorkspaceRoot workspace, IToolApprover approver, IWorkspaceObserver observer)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _approver = approver ?? throw new ArgumentNullException(nameof(approver));
        _observer = observer ?? throw new ArgumentNullException(nameof(observer));
    }

    /// <inheritdoc />
    public string Id => "edit_file";

    /// <inheritdoc />
    public string Name => "edit_file";

    /// <inheritdoc />
    public string Description =>
        "Edit a workspace file by replacing an exact string. old_string must match exactly once "
        + "(include surrounding lines to disambiguate) unless replace_all is true. "
        + "The user reviews a diff and may deny the edit.";

    /// <inheritdoc />
    public string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Workspace-relative file path." },
            "old_string": { "type": "string", "description": "Exact text to replace." },
            "new_string": { "type": "string", "description": "Replacement text." },
            "replace_all": { "type": "boolean", "description": "Replace every occurrence (default false)." }
          },
          "required": ["path", "old_string", "new_string"]
        }
        """;

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        string path;
        string oldString;
        string? newString;
        var replaceAll = false;
        try
        {
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            path = doc.RootElement.TryGetProperty("path", out var p) ? p.GetString() ?? string.Empty : string.Empty;
            oldString = doc.RootElement.TryGetProperty("old_string", out var o) ? o.GetString() ?? string.Empty : string.Empty;
            newString = doc.RootElement.TryGetProperty("new_string", out var n) ? n.GetString() : null;
            if (doc.RootElement.TryGetProperty("replace_all", out var r) && r.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                replaceAll = r.GetBoolean();
            }
        }
        catch (JsonException ex)
        {
            return new ToolResult { Success = false, Error = $"invalid arguments: {ex.Message}" };
        }

        if (string.IsNullOrWhiteSpace(path) || oldString.Length == 0 || newString is null)
        {
            return new ToolResult
            {
                Success = false,
                Error = "the 'path', 'old_string' and 'new_string' arguments are required",
            };
        }

        if (string.Equals(oldString, newString, StringComparison.Ordinal))
        {
            return new ToolResult { Success = false, Error = "old_string and new_string are identical" };
        }

        string full;
        try
        {
            full = _workspace.ResolveSafe(path);
        }
        catch (ArgumentException ex)
        {
            return new ToolResult { Success = false, Error = ex.Message };
        }

        var relative = _workspace.ToRelative(full);
        if (!File.Exists(full))
        {
            return new ToolResult { Success = false, Error = $"file not found: {relative}" };
        }

        var oldText = await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false);
        var occurrences = CountOccurrences(oldText, oldString);
        if (occurrences == 0)
        {
            return new ToolResult { Success = false, Error = $"old_string not found in {relative}" };
        }

        if (occurrences > 1 && !replaceAll)
        {
            return new ToolResult
            {
                Success = false,
                Error = $"old_string matches {occurrences} times in {relative} — "
                    + "extend it to a unique anchor or pass replace_all",
            };
        }

        var newText = replaceAll
            ? oldText.Replace(oldString, newString, StringComparison.Ordinal)
            : ReplaceFirst(oldText, oldString, newString);

        var diff = UnifiedDiff.Compute(oldText, newText, relative);
        var request = new ApprovalRequest
        {
            Kind = Id,
            Path = relative,
            OldText = oldText,
            NewText = newText,
            UnifiedDiff = diff,
        };

        var decision = _approver.AutoApprove
            ? ApprovalDecision.Approve
            : await _approver.RequestAsync(request, cancellationToken).ConfigureAwait(false);

        if (decision == ApprovalDecision.Deny)
        {
            return new ToolResult
            {
                Success = false,
                ResultJson = JsonSerializer.Serialize(new { denied = true, path = relative, diff }),
                Error = "the user denied this edit",
            };
        }

        await File.WriteAllTextAsync(full, newText, cancellationToken).ConfigureAwait(false);
        _observer.OnEditApplied(request);

        var replaced = replaceAll ? occurrences : 1;
        return new ToolResult
        {
            Success = true,
            ResultJson = JsonSerializer.Serialize(new { path = relative, replacements = replaced, diff }),
            Stdout = $"edited {relative} ({replaced} replacement{(replaced == 1 ? string.Empty : "s")})",
        };
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string ReplaceFirst(string text, string needle, string replacement)
    {
        var index = text.IndexOf(needle, StringComparison.Ordinal);
        return text[..index] + replacement + text[(index + needle.Length)..];
    }
}
