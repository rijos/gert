using System.Text.Json;
using Gert.Service.Tools;

namespace Gert.Console.Tools;

/// <summary>
/// The TUI's gated file writer (U16). Model function <c>write_file</c>: create
/// or overwrite a file inside the <see cref="WorkspaceRoot"/>. The write is
/// gated through <see cref="IToolApprover"/> (the TUI shows the unified diff);
/// a denial returns the diff as a tool error the model can adapt to —
/// SandboxTool's failure discipline throughout. Console-only.
/// </summary>
public sealed class WriteFileTool : ITool
{
    private readonly WorkspaceRoot _workspace;
    private readonly IToolApprover _approver;
    private readonly IWorkspaceObserver _observer;

    public WriteFileTool(WorkspaceRoot workspace, IToolApprover approver, IWorkspaceObserver observer)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _approver = approver ?? throw new ArgumentNullException(nameof(approver));
        _observer = observer ?? throw new ArgumentNullException(nameof(observer));
    }

    /// <inheritdoc />
    public string Id => "write_file";

    /// <inheritdoc />
    public string Name => "write_file";

    /// <inheritdoc />
    public string Description =>
        "Create or overwrite a file in the local workspace with the given content. "
        + "The user reviews a diff and may deny the write. Prefer edit_file for small changes.";

    /// <inheritdoc />
    public string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Workspace-relative file path." },
            "content": { "type": "string", "description": "The full new file content." }
          },
          "required": ["path", "content"]
        }
        """;

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        string path;
        string? content;
        try
        {
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            path = doc.RootElement.TryGetProperty("path", out var p) ? p.GetString() ?? string.Empty : string.Empty;
            content = doc.RootElement.TryGetProperty("content", out var c) ? c.GetString() : null;
        }
        catch (JsonException ex)
        {
            return new ToolResult { Success = false, Error = $"invalid arguments: {ex.Message}" };
        }

        if (string.IsNullOrWhiteSpace(path) || content is null)
        {
            return new ToolResult { Success = false, Error = "the 'path' and 'content' arguments are required" };
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
        var exists = File.Exists(full);
        var oldText = exists
            ? await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false)
            : null;

        if (string.Equals(oldText, content, StringComparison.Ordinal))
        {
            return new ToolResult
            {
                Success = true,
                ResultJson = JsonSerializer.Serialize(new { path = relative, unchanged = true }),
                Stdout = $"{relative} already has that content — no write needed",
            };
        }

        var diff = UnifiedDiff.Compute(oldText, content, relative);
        var request = new ApprovalRequest
        {
            Kind = Id,
            Path = relative,
            OldText = oldText,
            NewText = content,
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
                Error = "the user denied this write",
            };
        }

        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(full, content, cancellationToken).ConfigureAwait(false);
        _observer.OnEditApplied(request);

        var lines = content.Length == 0 ? 0 : content.Split('\n').Length;
        return new ToolResult
        {
            Success = true,
            ResultJson = JsonSerializer.Serialize(new { path = relative, created = !exists, lines }),
            Stdout = $"{(exists ? "rewrote" : "created")} {relative} ({lines} lines)",
        };
    }
}
