using System.Text.Json;
using Gert.Service.Tools;

namespace Gert.Console.Tools;

/// <summary>
/// The TUI's local file reader (U16). Model function <c>read_file</c>: returns
/// a slice of a text file inside the <see cref="WorkspaceRoot"/>. Console-only
/// — never registered in the API host, where there is no local workspace.
/// Failure shapes mirror <c>SandboxTool</c>: bad args / escapes / missing files
/// are tool errors the model can read, never a torn-down turn.
/// </summary>
public sealed class ReadFileTool : ITool
{
    /// <summary>Max lines returned in one call (ask again with offset to page).</summary>
    private const int MaxLines = 2000;

    /// <summary>Max bytes a file may have before it must be paged with offset/limit.</summary>
    private const long MaxBytes = 4 * 1024 * 1024;

    private readonly WorkspaceRoot _workspace;

    public ReadFileTool(WorkspaceRoot workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    /// <inheritdoc />
    public string Id => "read_file";

    /// <inheritdoc />
    public string Name => "read_file";

    /// <inheritdoc />
    public string Description =>
        "Read a text file from the local workspace. Returns numbered lines; use offset/limit "
        + "to page through large files.";

    /// <inheritdoc />
    public string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Workspace-relative file path." },
            "offset": { "type": "integer", "description": "1-based line to start from (default 1)." },
            "limit": { "type": "integer", "description": "Max lines to return (default 2000)." }
          },
          "required": ["path"]
        }
        """;

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        string path;
        var offset = 1;
        var limit = MaxLines;
        try
        {
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            path = doc.RootElement.TryGetProperty("path", out var p) ? p.GetString() ?? string.Empty : string.Empty;
            if (doc.RootElement.TryGetProperty("offset", out var o) && o.TryGetInt32(out var ov) && ov > 0)
            {
                offset = ov;
            }

            if (doc.RootElement.TryGetProperty("limit", out var l) && l.TryGetInt32(out var lv) && lv > 0)
            {
                limit = Math.Min(lv, MaxLines);
            }
        }
        catch (JsonException ex)
        {
            return new ToolResult { Success = false, Error = $"invalid arguments: {ex.Message}" };
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return new ToolResult { Success = false, Error = "the 'path' argument is required" };
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

        if (!File.Exists(full))
        {
            return new ToolResult { Success = false, Error = $"file not found: {path}" };
        }

        if (new FileInfo(full).Length > MaxBytes)
        {
            return new ToolResult
            {
                Success = false,
                Error = $"file exceeds {MaxBytes / (1024 * 1024)} MB; not a readable text file",
            };
        }

        var lines = await File.ReadAllLinesAsync(full, cancellationToken).ConfigureAwait(false);
        var slice = lines.Skip(offset - 1).Take(limit).ToArray();
        var truncated = offset - 1 + slice.Length < lines.Length;

        var resultJson = JsonSerializer.Serialize(new
        {
            path = _workspace.ToRelative(full),
            content = string.Join('\n', slice),
            start_line = offset,
            total_lines = lines.Length,
            truncated,
        });

        return new ToolResult
        {
            Success = true,
            ResultJson = resultJson,
            // The card shows what happened, not the file body (the model reads ResultJson).
            Stdout = $"read {_workspace.ToRelative(full)} — lines {offset}–{offset - 1 + slice.Length} of {lines.Length}",
        };
    }
}
