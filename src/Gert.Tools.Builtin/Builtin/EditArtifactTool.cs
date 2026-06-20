using Gert.Database;
using Gert.Service;
using Gert.Tools;
using Gert.Validation;

namespace Gert.Tools.Builtin;

/// <summary>
/// The canvas scratchpad tool. Model function <c>edit_artifact</c>: change part of an
/// existing artifact by exact substring replacement (<c>old_str</c> to <c>new_str</c>) so
/// the model iterates without re-emitting the whole file. Mirrors Anthropic's
/// <c>str_replace</c> contract: <c>old_str</c> must match EXACTLY (whitespace included) and
/// EXACTLY ONCE; zero or many matches return an error the model corrects next round. The
/// updated artifact rides back on <see cref="ToolCallResult{TResult}.Artifacts"/> with its
/// original id, so the canvas tab updates in place.
/// </summary>
public sealed class EditArtifactTool : ToolCall<EditArtifactArgs, EditArtifactResult>
{
    private readonly IChatDatabaseProvider _databases;
    private readonly IUserContext _user;

    public EditArtifactTool(IValidationProvider validation, IChatDatabaseProvider databases, IUserContext user)
        : base(validation)
    {
        _databases = databases ?? throw new ArgumentNullException(nameof(databases));
        _user = user ?? throw new ArgumentNullException(nameof(user));
    }

    /// <inheritdoc />
    public override string Id => "edit_artifact";

    /// <inheritdoc />
    public override string Name => "edit_artifact";

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

        if (string.IsNullOrEmpty(invocation.ConversationId))
        {
            return ToolCallResult<EditArtifactResult>.Fail("edit_artifact needs a conversation context");
        }

        var oldStr = args.OldStr;
        var newStr = args.NewStr ?? string.Empty;

        await using var repo = await _databases
            .OpenAsync(_user.Iss, _user.Sub, invocation.Pid, cancellationToken)
            .ConfigureAwait(false);

        var artifact = await repo.GetArtifactByNameAsync(invocation.ConversationId, args.Name, cancellationToken)
            .ConfigureAwait(false);
        if (artifact is null)
        {
            return ToolCallResult<EditArtifactResult>.Fail(
                $"no artifact named '{args.Name}'. Create it with make_artifact, or check the name.");
        }

        var matches = CountOccurrences(artifact.Content, oldStr);
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

        var updated = artifact with
        {
            Content = ReplaceFirst(artifact.Content, oldStr, newStr),
            Version = artifact.Version + 1,
        };
        await repo.UpdateArtifactAsync(updated, cancellationToken).ConfigureAwait(false);

        var payload = new EditArtifactResult
        {
            Name = updated.Name,
            Action = "edited",
            Version = updated.Version,
        };

        return ToolCallResult<EditArtifactResult>.Ok(
            payload, stdout: $"edited {updated.Name}", artifacts: [updated]);
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
