using System.Text.Json;
using Gert.Service.Tools;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace Gert.Console.Tools;

/// <summary>
/// The TUI's local file finder (U16). Model function <c>glob</c>: match files
/// under the <see cref="WorkspaceRoot"/> by glob pattern (e.g.
/// <c>**/*.cs</c>). <c>.git</c> is always excluded. Console-only; failure
/// shapes mirror <c>SandboxTool</c>.
/// </summary>
public sealed class GlobTool : ITool
{
    private const int MaxMatches = 500;

    private readonly WorkspaceRoot _workspace;

    public GlobTool(WorkspaceRoot workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    /// <inheritdoc />
    public string Id => "glob";

    /// <inheritdoc />
    public string Name => "glob";

    /// <inheritdoc />
    public string Description =>
        "Find files in the local workspace by glob pattern (e.g. '**/*.cs', 'src/**/Program.*'). "
        + "Returns workspace-relative paths; .git is excluded.";

    /// <inheritdoc />
    public string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "pattern": { "type": "string", "description": "Glob pattern, e.g. '**/*.cs'." },
            "path": { "type": "string", "description": "Workspace-relative directory to search from (default '.')." }
          },
          "required": ["pattern"]
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
        string pattern;
        string path;
        try
        {
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            pattern = doc.RootElement.TryGetProperty("pattern", out var g) ? g.GetString() ?? string.Empty : string.Empty;
            path = doc.RootElement.TryGetProperty("path", out var p) ? p.GetString() ?? "." : ".";
        }
        catch (JsonException ex)
        {
            return new ToolResult { Success = false, Error = $"invalid arguments: {ex.Message}" };
        }

        if (string.IsNullOrWhiteSpace(pattern))
        {
            return new ToolResult { Success = false, Error = "the 'pattern' argument is required" };
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

        var matcher = new Matcher(StringComparison.Ordinal);
        matcher.AddInclude(pattern);
        matcher.AddExclude(".git/**");

        var matches = matcher
            .Execute(new DirectoryInfoWrapper(new DirectoryInfo(full)))
            .Files
            .Select(f => Path.Combine(_workspace.ToRelative(full), f.Path))
            .Select(p => p.StartsWith("./", StringComparison.Ordinal) ? p[2..] : p)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Take(MaxMatches + 1)
            .ToList();
        var truncated = matches.Count > MaxMatches;
        if (truncated)
        {
            matches.RemoveAt(matches.Count - 1);
        }

        var resultJson = JsonSerializer.Serialize(new { pattern, matches, truncated });

        return new ToolResult
        {
            Success = true,
            ResultJson = resultJson,
            Stdout = matches.Count == 0 ? "(no matches)" : string.Join('\n', matches),
        };
    }
}
