using System.Text.Json;
using Gert.Database;
using Gert.Model.Chat;
using Gert.Service;
using Gert.Service.Tools;

namespace Gert.Tools.Builtin;

/// <summary>
/// The canvas create tool. Model function <c>make_artifact</c>: the model writes a
/// complete, self-contained file as a tool argument rather than a fenced code block, so
/// the content is opaque JSON whose own ` ``` ` fences can't truncate it (the nested-fence
/// bug the fenced-block convention had). Create-or-overwrite by <c>name</c> within the
/// conversation - a re-used name saves over the prior draft (same canvas tab, bumped
/// version). The artifact rides back on <see cref="ToolResult.Artifacts"/>; the
/// orchestrator emits the <c>ArtifactEvent</c> that opens/updates the tab.
/// </summary>
public sealed class MakeArtifactTool : ITool
{
    private readonly IChatDatabaseProvider _databases;
    private readonly IUserContext _user;
    private readonly TimeProvider _time;

    public MakeArtifactTool(IChatDatabaseProvider databases, IUserContext user, TimeProvider time)
    {
        _databases = databases ?? throw new ArgumentNullException(nameof(databases));
        _user = user ?? throw new ArgumentNullException(nameof(user));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <inheritdoc />
    public string Id => "make_artifact";

    /// <inheritdoc />
    public string Name => "make_artifact";

    /// <inheritdoc />
    public string Description =>
        "Create a complete, self-contained file (HTML page, script, Markdown, SVG, "
        + "...) that opens in the user's canvas. Always use this instead of putting "
        + "a whole file in a code block; re-using a name overwrites that artifact.";

    /// <inheritdoc />
    public string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "name": { "type": "string", "description": "File name with extension, e.g. index.html or notes.md." },
            "format": { "type": "string", "enum": ["html", "markdown", "svg", "python", "csharp", "cpp", "javascript", "rust"] },
            "content": { "type": "string", "description": "The entire file content." }
          },
          "required": ["name", "format", "content"]
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
            return new ToolResult { Success = false, Error = "make_artifact needs a conversation context" };
        }

        string? name, format, content;
        try
        {
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            var root = doc.RootElement;
            name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
            format = root.TryGetProperty("format", out var f) ? f.GetString() : null;
            content = root.TryGetProperty("content", out var c) ? c.GetString() : null;
        }
        catch (JsonException ex)
        {
            return new ToolResult { Success = false, Error = $"invalid arguments: {ex.Message}" };
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return new ToolResult { Success = false, Error = "name is required" };
        }

        if (string.IsNullOrEmpty(content))
        {
            return new ToolResult { Success = false, Error = "content is required" };
        }

        if (ArtifactFormat.ToKind(format) is not { } kind)
        {
            return new ToolResult
            {
                Success = false,
                Error = $"unknown format '{format}'; use one of: {string.Join(", ", ArtifactFormat.Canonical)}",
            };
        }

        await using var repo = await _databases
            .OpenAsync(_user.Iss, _user.Sub, invocation.Pid, cancellationToken)
            .ConfigureAwait(false);

        var existing = await repo.GetArtifactByNameAsync(invocation.ConversationId, name, cancellationToken)
            .ConfigureAwait(false);

        Artifact artifact;
        string action;
        if (existing is not null)
        {
            artifact = existing with
            {
                Kind = kind,
                Language = ArtifactFormat.FromKind(kind),
                Content = content,
                Version = existing.Version + 1,
            };
            await repo.UpdateArtifactAsync(artifact, cancellationToken).ConfigureAwait(false);
            action = "updated";
        }
        else
        {
            artifact = new Artifact
            {
                Id = Guid.NewGuid().ToString("D"),
                ConversationId = invocation.ConversationId,
                MessageId = invocation.MessageId,
                Kind = kind,
                Name = name,
                Language = ArtifactFormat.FromKind(kind),
                Content = content,
                Version = 1,
                CreatedAt = _time.GetUtcNow(),
            };
            await repo.InsertArtifactAsync(artifact, cancellationToken).ConfigureAwait(false);
            action = "created";
        }

        return new ToolResult
        {
            Success = true,
            ResultJson = JsonSerializer.Serialize(new
            {
                name = artifact.Name,
                format = ArtifactFormat.FromKind(kind),
                action,
                version = artifact.Version,
            }),
            Stdout = $"{action} {artifact.Name}",
            Artifacts = [artifact],
        };
    }
}
