using System.Text;
using System.Text.Json;
using Gert.Database;

namespace Gert.Service.Tools;

/// <summary>
/// The canvas read tool. Model function <c>read_artifact</c>: return an existing
/// artifact's current content so the model can iterate on it across rounds/turns
/// without re-deriving it. Lines are 1-indexed and prefixed with their number
/// (mirrors Anthropic's <c>view</c>) so a follow-up <c>edit_artifact</c> can copy a
/// snippet verbatim and an optional <c>range</c> is addressable. Read-only — emits
/// no canvas event.
/// </summary>
public sealed class ReadArtifactTool : ITool
{
    private readonly IChatDatabaseProvider _databases;
    private readonly IUserContext _user;

    public ReadArtifactTool(IChatDatabaseProvider databases, IUserContext user)
    {
        _databases = databases ?? throw new ArgumentNullException(nameof(databases));
        _user = user ?? throw new ArgumentNullException(nameof(user));
    }

    /// <inheritdoc />
    public string Id => "read_artifact";

    /// <inheritdoc />
    public string Name => "read_artifact";

    /// <inheritdoc />
    public string Description =>
        "Read the current content of an existing canvas artifact (line-numbered). Use "
        + "before edit_artifact when you need to see what's there, or to recall an artifact "
        + "from earlier in the conversation. Optionally pass a [start, end] line range "
        + "(1-indexed; end -1 means to the end) to read just part of a large file.";

    /// <inheritdoc />
    public string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "name": { "type": "string", "description": "Name of the artifact to read." },
            "range": {
              "type": "array",
              "description": "Optional [start, end] line numbers, 1-indexed; end -1 reads to the end.",
              "items": { "type": "integer" },
              "minItems": 2,
              "maxItems": 2
            }
          },
          "required": ["name"]
        }
        """;

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        if (string.IsNullOrEmpty(invocation.ConversationId))
        {
            return new ToolResult { Success = false, Error = "read_artifact needs a conversation context" };
        }

        string? name;
        int? start = null, end = null;
        try
        {
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            var root = doc.RootElement;
            name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (root.TryGetProperty("range", out var r) && r.ValueKind == JsonValueKind.Array && r.GetArrayLength() == 2)
            {
                start = r[0].GetInt32();
                end = r[1].GetInt32();
            }
        }
        catch (JsonException ex)
        {
            return new ToolResult { Success = false, Error = $"invalid arguments: {ex.Message}" };
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return new ToolResult { Success = false, Error = "name is required" };
        }

        await using var repo = await _databases
            .OpenAsync(_user.Iss, _user.Sub, invocation.Pid, cancellationToken)
            .ConfigureAwait(false);

        var artifact = await repo.GetArtifactByNameAsync(invocation.ConversationId, name, cancellationToken)
            .ConfigureAwait(false);
        if (artifact is null)
        {
            return new ToolResult { Success = false, Error = $"no artifact named '{name}'." };
        }

        var lines = artifact.Content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        // Resolve the 1-indexed inclusive window (default = whole file; -1 end = last line).
        var from = start is { } s ? Math.Max(1, s) : 1;
        var to = end is { } e ? (e < 0 ? lines.Length : Math.Min(lines.Length, e)) : lines.Length;

        var sb = new StringBuilder();
        for (var i = from; i <= to && i <= lines.Length; i++)
        {
            sb.Append(i).Append('\t').Append(lines[i - 1]).Append('\n');
        }

        return new ToolResult
        {
            Success = true,
            ResultJson = JsonSerializer.Serialize(new
            {
                name = artifact.Name,
                format = ArtifactFormat.FromKind(artifact.Kind),
                version = artifact.Version,
                line_count = lines.Length,
                content = sb.ToString(),
            }),
        };
    }
}
