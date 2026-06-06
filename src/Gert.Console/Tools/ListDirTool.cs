using System.Globalization;
using System.Text;
using System.Text.Json;
using Gert.Service.Tools;

namespace Gert.Console.Tools;

/// <summary>
/// The TUI's local directory lister (U16). Model function <c>list_dir</c>:
/// entries of one directory inside the <see cref="WorkspaceRoot"/>,
/// directories first. Console-only; failure shapes mirror <c>SandboxTool</c>.
/// </summary>
public sealed class ListDirTool : ITool
{
    private const int MaxEntries = 500;

    private readonly WorkspaceRoot _workspace;

    public ListDirTool(WorkspaceRoot workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    /// <inheritdoc />
    public string Id => "list_dir";

    /// <inheritdoc />
    public string Name => "list_dir";

    /// <inheritdoc />
    public string Description =>
        "List the entries of a directory in the local workspace (directories first). "
        + "Defaults to the workspace root.";

    /// <inheritdoc />
    public string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Workspace-relative directory (default '.')." }
          }
        }
        """;

    /// <inheritdoc />
    public Task<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        return Task.FromResult(Execute(invocation));
    }

    private ToolResult Execute(ToolInvocation invocation)
    {
        string path;
        try
        {
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            path = doc.RootElement.TryGetProperty("path", out var p) ? p.GetString() ?? "." : ".";
        }
        catch (JsonException ex)
        {
            return new ToolResult { Success = false, Error = $"invalid arguments: {ex.Message}" };
        }

        string full;
        try
        {
            full = _workspace.ResolveSafe(string.IsNullOrWhiteSpace(path) ? "." : path);
        }
        catch (ArgumentException ex)
        {
            return new ToolResult { Success = false, Error = ex.Message };
        }

        if (!Directory.Exists(full))
        {
            return new ToolResult { Success = false, Error = $"directory not found: {path}" };
        }

        var entries = new DirectoryInfo(full)
            .EnumerateFileSystemInfos()
            .OrderByDescending(e => e is DirectoryInfo)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .Take(MaxEntries + 1)
            .ToList();
        var truncated = entries.Count > MaxEntries;
        if (truncated)
        {
            entries.RemoveAt(entries.Count - 1);
        }

        var stdout = new StringBuilder();
        var rows = new List<object>(entries.Count);
        foreach (var entry in entries)
        {
            var dir = entry is DirectoryInfo;
            var size = entry is FileInfo f ? f.Length : 0L;
            rows.Add(new { name = entry.Name, kind = dir ? "dir" : "file", size });
            stdout.Append(dir ? entry.Name + "/" : entry.Name);
            if (!dir)
            {
                stdout.Append("  ").Append(size.ToString("N0", CultureInfo.InvariantCulture));
            }

            stdout.Append('\n');
        }

        var resultJson = JsonSerializer.Serialize(new
        {
            path = _workspace.ToRelative(full),
            entries = rows,
            truncated,
        });

        return new ToolResult
        {
            Success = true,
            ResultJson = resultJson,
            Stdout = stdout.Length == 0 ? "(empty)" : stdout.ToString().TrimEnd('\n'),
        };
    }
}
