using Gert.Database;
using Gert.Model.Chat;
using Gert.Service;
using Gert.Tools;
using Gert.Validation;

namespace Gert.Tools.Builtin;

/// <summary>
/// The canvas create tool. Model function <c>make_artifact</c>: the model writes a
/// complete, self-contained file as a tool argument rather than a fenced code block, so
/// the content is opaque JSON whose own ` ``` ` fences can't truncate it (the nested-fence
/// bug the fenced-block convention had). Create-or-overwrite by <c>name</c> within the
/// conversation - a re-used name saves over the prior draft (same canvas tab, bumped
/// version). The artifact rides back on <see cref="ToolCallResult{TResult}.Artifacts"/>;
/// the orchestrator emits the <c>ArtifactEvent</c> that opens/updates the tab.
/// </summary>
public sealed class MakeArtifactTool : ToolCall<MakeArtifactArgs, MakeArtifactResult>
{
    private readonly IChatDatabaseProvider _databases;
    private readonly IUserContext _user;
    private readonly TimeProvider _time;

    public MakeArtifactTool(IValidationProvider validation, IChatDatabaseProvider databases, IUserContext user, TimeProvider time)
        : base(validation)
    {
        _databases = databases ?? throw new ArgumentNullException(nameof(databases));
        _user = user ?? throw new ArgumentNullException(nameof(user));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <inheritdoc />
    public override string Id => "make_artifact";

    /// <inheritdoc />
    public override string Name => "make_artifact";

    /// <inheritdoc />
    public override string Description =>
        "Create a complete, self-contained file (HTML page, script, Markdown, SVG, "
        + "...) that opens in the user's canvas. Always use this instead of putting "
        + "a whole file in a code block; re-using a name overwrites that artifact.";

    /// <inheritdoc />
    public override string ParametersSchema =>
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
    public override async Task<ToolCallResult<MakeArtifactResult>> CallAsync(
        MakeArtifactArgs args,
        ToolInvocation invocation,
        IToolHost host,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(invocation);

        if (string.IsNullOrEmpty(invocation.ConversationId))
        {
            return ToolCallResult<MakeArtifactResult>.Fail("make_artifact needs a conversation context");
        }

        // Format aliases (py/md/...) resolve here, not in the validator: the alias map
        // is an impl detail of this leaf, so an unknown format is a business error.
        if (ArtifactFormat.ToKind(args.Format) is not { } kind)
        {
            return ToolCallResult<MakeArtifactResult>.Fail(
                $"unknown format '{args.Format}'; use one of: {string.Join(", ", ArtifactFormat.Canonical)}");
        }

        await using var repo = await _databases
            .OpenAsync(_user.Iss, _user.Sub, invocation.Pid, cancellationToken)
            .ConfigureAwait(false);

        var existing = await repo.GetArtifactByNameAsync(invocation.ConversationId, args.Name, cancellationToken)
            .ConfigureAwait(false);

        Artifact artifact;
        string action;
        if (existing is not null)
        {
            artifact = existing with
            {
                Kind = kind,
                Language = ArtifactFormat.FromKind(kind),
                Content = args.Content,
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
                Name = args.Name,
                Language = ArtifactFormat.FromKind(kind),
                Content = args.Content,
                Version = 1,
                CreatedAt = _time.GetUtcNow(),
            };
            await repo.InsertArtifactAsync(artifact, cancellationToken).ConfigureAwait(false);
            action = "created";
        }

        var payload = new MakeArtifactResult
        {
            Name = artifact.Name,
            Format = ArtifactFormat.FromKind(kind),
            Action = action,
            Version = artifact.Version,
        };

        return ToolCallResult<MakeArtifactResult>.Ok(
            payload, stdout: $"{action} {artifact.Name}", artifacts: [artifact]);
    }
}
