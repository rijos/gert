using Gert.Model.Chat;
using Gert.Tools;
using Gert.Validation;

namespace Gert.Tools.Builtin;

/// <summary>
/// The canvas list tool. Model function <c>list_artifacts</c>: enumerate the
/// conversation's artifacts (name, format, version) so the model can pick the right one
/// to <c>read_artifact</c> or <c>edit_artifact</c> instead of guessing a name. Read-only -
/// emits no canvas event. Lists through the host's chat-scoped <see cref="IObjectResource"/>.
/// </summary>
public sealed class ListArtifactsTool(IValidationProvider validation)
    : ToolCall<ListArtifactsArgs, ListArtifactsResult>(validation)
{
    /// <inheritdoc />
    public override string Id => "list_artifacts";

    /// <inheritdoc />
    public override string Name => "list_artifacts";

    /// <inheritdoc />
    public override string Title => "List files";

    /// <inheritdoc />
    public override string Icon => "file";

    /// <inheritdoc />
    public override string Group => "canvas";

    /// <inheritdoc />
    public override string Description =>
        "List the canvas files in this conversation (name, format, version) so you can "
        + "read or edit the right one. Use when unsure what exists.";

    /// <inheritdoc />
    public override string ParametersSchema => """{"type":"object","properties":{}}""";

    /// <inheritdoc />
    public override async Task<ToolCallResult<ListArtifactsResult>> CallAsync(
        ListArtifactsArgs args,
        ToolInvocation invocation,
        IToolHost host,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(host);

        var objects = await host.Resources.Objects.ListAsync(ResourceScope.Chat, cancellationToken)
            .ConfigureAwait(false);

        var listed = objects
            .Select(o => new ListedArtifact
            {
                Name = o.Name,
                Format = ArtifactFormat.FromKind(ArtifactKinds.FromToken(o.Kind)),
                Version = o.Version,
            })
            .ToList();

        return ToolCallResult<ListArtifactsResult>.Ok(
            new ListArtifactsResult { Artifacts = listed },
            stdout: listed.Count == 0
                ? "No files yet."
                : string.Join("\n", listed.Select(a => $"{a.Name} (v{a.Version})")));
    }
}
