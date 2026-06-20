using Gert.Model.Chat;
using Gert.Tools;
using Gert.Tools.Args;
using Gert.Tools.Hosting;
using Gert.Tools.Resources;
using Gert.Tools.Results;
using Gert.Validation;

namespace Gert.Tools.Builtin;

/// <summary>
/// The canvas scratchpad tool. Model function <c>edit_artifact</c>: change part of an
/// existing artifact by exact substring replacement (<c>old_str</c> to <c>new_str</c>) so
/// the model iterates without re-emitting the whole file. Mirrors Anthropic's
/// <c>str_replace</c> contract: <c>old_str</c> must match EXACTLY (whitespace included) and
/// EXACTLY ONCE; zero or many matches return an error the model corrects next round. The
/// updated artifact rides back on <see cref="ToolCallResult{TResult}.Artifacts"/> with its
/// original id, so the canvas tab updates in place. Storage runs through the host's
/// chat-scoped <see cref="IObjectResource"/>.
/// </summary>
public sealed class EditArtifactTool(IValidationProvider validation)
    : ToolCall<EditArtifactArgs, EditArtifactResult>(validation)
{
    /// <inheritdoc />
    public override string Id => "edit_artifact";

    /// <inheritdoc />
    public override string Name => "edit_artifact";

    /// <inheritdoc />
    public override string Title => "Edit file";

    /// <inheritdoc />
    public override string Icon => "file";

    /// <inheritdoc />
    public override string Group => "canvas";

    /// <inheritdoc />
    public override string Description =>
        "Edit an existing canvas artifact by replacing an exact snippet - change "
        + "part of a file instead of remaking it. old_str must match exactly once, "
        + "verbatim; include more surrounding lines if it is not unique.";

    /// <inheritdoc />
    public override string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "name": { "type": "string", "description": "Name of the artifact to edit." },
            "old_str": { "type": "string", "description": "Exact text to find - must match a single location verbatim." },
            "new_str": { "type": "string", "description": "Replacement text (may be empty to delete the snippet)." }
          },
          "required": ["name", "old_str", "new_str"]
        }
        """;

    /// <inheritdoc />
    public override async Task<ToolCallResult<EditArtifactResult>> CallAsync(
        EditArtifactArgs args,
        ToolInvocation invocation,
        IToolHost host,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(host);

        var oldStr = args.OldStr;
        var newStr = args.NewStr ?? string.Empty;

        var stored = await host.Resources.Objects.GetAsync(ResourceScope.Chat, args.Name, cancellationToken)
            .ConfigureAwait(false);
        if (stored is null)
        {
            return ToolCallResult<EditArtifactResult>.Fail(
                $"no artifact named '{args.Name}'. Create it with make_artifact, or check the name.");
        }

        var matches = CountOccurrences(stored.Content, oldStr);
        if (matches == 0)
        {
            return ToolCallResult<EditArtifactResult>.Fail(
                $"old_str not found in '{args.Name}'. Use read_artifact to see the current content.");
        }

        if (matches > 1)
        {
            return ToolCallResult<EditArtifactResult>.Fail(
                $"old_str matches {matches} places in '{args.Name}'; include more surrounding context to make it unique.");
        }

        var newContent = ReplaceFirst(stored.Content, oldStr, newStr);
        var updated = await host.Resources.Objects.PutAsync(
            ResourceScope.Chat,
            new ObjectWrite { Name = args.Name, Content = newContent, Kind = stored.Kind },
            cancellationToken).ConfigureAwait(false);

        var kind = ArtifactKinds.FromToken(updated.Kind);
        var artifact = new Artifact
        {
            Id = updated.Id,
            ConversationId = invocation.ConversationId!,
            MessageId = invocation.MessageId,
            Kind = kind,
            Name = updated.Name,
            Language = ArtifactFormat.FromKind(kind),
            Content = updated.Content,
            Version = updated.Version,
            CreatedAt = updated.CreatedAt,
            UpdatedAt = updated.UpdatedAt,
        };

        var payload = new EditArtifactResult
        {
            Name = updated.Name,
            Action = "edited",
            Version = updated.Version,
        };

        return ToolCallResult<EditArtifactResult>.Ok(
            payload, stdout: $"edited {updated.Name}", artifacts: [artifact]);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string ReplaceFirst(string haystack, string needle, string replacement)
    {
        var i = haystack.IndexOf(needle, StringComparison.Ordinal);
        return i < 0 ? haystack : string.Concat(haystack.AsSpan(0, i), replacement, haystack.AsSpan(i + needle.Length));
    }
}
