using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gert.Service.Tools;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace Gert.Console.Tools;

/// <summary>
/// The TUI's local content search (U16). Model function <c>grep</c>: regex
/// search across workspace files, optionally filtered by a glob. Binary files
/// and <c>.git</c> are skipped; the regex runs with a match timeout so a
/// pathological pattern degrades to a tool error, never a hung turn.
/// Console-only.
/// </summary>
public sealed class GrepTool : ITool
{
    private const int MaxHits = 200;
    private const long MaxFileBytes = 4 * 1024 * 1024;

    private readonly WorkspaceRoot _workspace;

    public GrepTool(WorkspaceRoot workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    /// <inheritdoc />
    public string Id => "grep";

    /// <inheritdoc />
    public string Name => "grep";

    /// <inheritdoc />
    public string Description =>
        "Search file contents in the local workspace with a regular expression. "
        + "Optionally restrict files with a glob (e.g. '**/*.cs'). Returns path:line matches.";

    /// <inheritdoc />
    public string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "pattern": { "type": "string", "description": ".NET regular expression to search for." },
            "path": { "type": "string", "description": "Workspace-relative directory to search from (default '.')." },
            "glob": { "type": "string", "description": "Glob filter for files to search (default '**/*')." }
          },
          "required": ["pattern"]
        }
        """;

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        string pattern;
        string path;
        string glob;
        try
        {
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            pattern = doc.RootElement.TryGetProperty("pattern", out var r) ? r.GetString() ?? string.Empty : string.Empty;
            path = doc.RootElement.TryGetProperty("path", out var p) ? p.GetString() ?? "." : ".";
            glob = doc.RootElement.TryGetProperty("glob", out var g) ? g.GetString() ?? "**/*" : "**/*";
        }
        catch (JsonException ex)
        {
            return new ToolResult { Success = false, Error = $"invalid arguments: {ex.Message}" };
        }

        if (string.IsNullOrWhiteSpace(pattern))
        {
            return new ToolResult { Success = false, Error = "the 'pattern' argument is required" };
        }

        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            return new ToolResult { Success = false, Error = $"invalid regex: {ex.Message}" };
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
        matcher.AddInclude(string.IsNullOrWhiteSpace(glob) ? "**/*" : glob);
        matcher.AddExclude(".git/**");
        var files = matcher
            .Execute(new DirectoryInfoWrapper(new DirectoryInfo(full)))
            .Files
            .Select(f => f.Path)
            .OrderBy(p => p, StringComparer.Ordinal);

        var hits = new List<object>();
        var stdout = new StringBuilder();
        var truncated = false;
        try
        {
            foreach (var relative in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var file = Path.Combine(full, relative);
                if (!File.Exists(file) || new FileInfo(file).Length > MaxFileBytes || await IsBinaryAsync(file, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                var display = Path.Combine(_workspace.ToRelative(full), relative);
                display = display.StartsWith("./", StringComparison.Ordinal) ? display[2..] : display;

                var lineNo = 0;
                foreach (var line in await File.ReadAllLinesAsync(file, cancellationToken).ConfigureAwait(false))
                {
                    lineNo++;
                    if (!regex.IsMatch(line))
                    {
                        continue;
                    }

                    if (hits.Count >= MaxHits)
                    {
                        truncated = true;
                        break;
                    }

                    var text = line.Length > 400 ? line[..400] : line;
                    hits.Add(new { path = display, line = lineNo, text });
                    stdout.Append(display).Append(':').Append(lineNo).Append(": ").Append(text).Append('\n');
                }

                if (truncated)
                {
                    break;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return new ToolResult { Success = false, Error = "regex timed out — simplify the pattern" };
        }

        var resultJson = JsonSerializer.Serialize(new { pattern, hits, truncated });

        return new ToolResult
        {
            Success = true,
            ResultJson = resultJson,
            Stdout = hits.Count == 0 ? "(no matches)" : stdout.ToString().TrimEnd('\n'),
        };
    }

    /// <summary>NUL byte in the first 8 KB ⇒ treat as binary and skip.</summary>
    private static async Task<bool> IsBinaryAsync(string file, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        await using var stream = File.OpenRead(file);
        var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.AsSpan(0, read).Contains((byte)0);
    }
}
