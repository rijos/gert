using Gert.Model.Chat;
using Gert.Tools;
using Gert.Tools.Args;
using Gert.Tools.Hosting;
using Gert.Tools.Resources;
using Gert.Tools.Results;
using Gert.Validation;

namespace Gert.Tools.Builtin;

/// <summary>
/// The canvas create tool. Model function <c>make_artifact</c>: the model writes a
/// complete, self-contained file as a tool argument rather than a fenced code block, so
/// the content is opaque JSON whose own ` ``` ` fences can't truncate it (the nested-fence
/// bug the fenced-block convention had). Create-or-overwrite by <c>name</c> within the
/// conversation - a re-used name saves over the prior draft (same canvas tab, bumped
/// version). The artifact rides back on <see cref="ToolCallResult{TResult}.Artifacts"/>;
/// the orchestrator emits the <c>ArtifactEvent</c> that opens/updates the tab. Storage runs
/// through the host's chat-scoped <see cref="IObjectResource"/> - the tool never sees an
/// identity or a storage key.
/// </summary>
public sealed class MakeArtifactTool(IValidationProvider validation)
    : ToolCall<MakeArtifactArgs, MakeArtifactResult>(validation)
{
    /// <inheritdoc />
    public override string Id => "make_artifact";

    /// <inheritdoc />
    public override string Name => "make_artifact";

    /// <inheritdoc />
    public override string Title => "Create file";

    /// <inheritdoc />
    public override string Icon => "file";

    /// <inheritdoc />
    public override string Group => "canvas";

    /// <inheritdoc />
    public override string Description =>
        "Create a complete, self-contained file (HTML page, script, Markdown, SVG, "
        + "...) that opens in the user's canvas. Always use this instead of putting "
        + "a whole file in a code block; re-using a name overwrites that artifact.";

    /// <inheritdoc />
    public override async Task<ToolCallResult<MakeArtifactResult>> CallAsync(
        MakeArtifactArgs args,
        ToolInvocation invocation,
        IToolHost host,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(host);

        // Format aliases (py/md/...) resolve here, not in the validator: the alias map
        // is an impl detail of this leaf, so an unknown format is a business error.
        if (ArtifactFormat.ToKind(args.Format) is not { } kind)
        {
            return ToolCallResult<MakeArtifactResult>.Fail(
                $"unknown format '{args.Format}'; use one of: {string.Join(", ", ArtifactFormat.Canonical)}");
        }

        // Create-or-overwrite by name; the resource bumps the version and preserves the id.
        var stored = await host.Resources.Objects.PutAsync(
            ResourceScope.Chat,
            new ObjectWrite { Name = args.Name, Content = args.Content, Kind = ArtifactKinds.ToToken(kind) },
            cancellationToken).ConfigureAwait(false);

        var action = stored.Version == 1 ? "created" : "updated";

        var artifact = new Artifact
        {
            Id = stored.Id,
            ConversationId = invocation.ConversationId!,
            MessageId = invocation.MessageId,
            Kind = kind,
            Name = stored.Name,
            Language = ArtifactFormat.FromKind(kind),
            Content = stored.Content,
            Version = stored.Version,
            CreatedAt = stored.CreatedAt,
            UpdatedAt = stored.UpdatedAt,
        };

        var payload = new MakeArtifactResult
        {
            Name = stored.Name,
            Format = ArtifactFormat.FromKind(kind),
            Action = action,
            Version = stored.Version,
        };

        return ToolCallResult<MakeArtifactResult>.Ok(
            payload, stdout: $"{action} {stored.Name}", artifacts: [artifact]);
    }
}
