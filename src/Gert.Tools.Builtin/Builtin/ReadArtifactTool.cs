using System.Text;
using Gert.Database;
using Gert.Service;
using Gert.Tools;
using Gert.Validation;

namespace Gert.Tools.Builtin;

/// <summary>
/// The canvas read tool. Model function <c>read_artifact</c>: return an existing
/// artifact's current content so the model can iterate on it across rounds/turns
/// without re-deriving it. Lines are 1-indexed and prefixed with their number
/// (mirrors Anthropic's <c>view</c>) so a follow-up <c>edit_artifact</c> can copy a
/// snippet verbatim and an optional <c>range</c> is addressable. Read-only - emits
/// no canvas event.
/// </summary>
public sealed class ReadArtifactTool : ToolCall<ReadArtifactArgs, ReadArtifactResult>
{
    private readonly IChatDatabaseProvider _databases;
    private readonly IUserContext _user;

    public ReadArtifactTool(IValidationProvider validation, IChatDatabaseProvider databases, IUserContext user)
        : base(validation)
    {
        _databases = databases ?? throw new ArgumentNullException(nameof(databases));
        _user = user ?? throw new ArgumentNullException(nameof(user));
    }

    /// <inheritdoc />
    public override string Id => "read_artifact";

    /// <inheritdoc />
    public override string Name => "read_artifact";

    /// <inheritdoc />
    public override string Description =>
        "Read the current content of an existing canvas artifact, line-numbered, "
        + "optionally limited to a [start, end] line range. Use before edit_artifact "
        + "when unsure of the current content.";

    /// <inheritdoc />
    public override string ParametersSchema =>
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
    public override async Task<ToolCallResult<ReadArtifactResult>> CallAsync(
        ReadArtifactArgs args,
        ToolInvocation invocation,
        IToolHost host,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(invocation);

        if (string.IsNullOrEmpty(invocation.ConversationId))
        {
            return ToolCallResult<ReadArtifactResult>.Fail("read_artifact needs a conversation context");
        }

        // A non-[start, end] range (wrong length) is ignored, not errored - it reads
        // the whole file, as before. A non-integer entry never reaches here: the typed
        // deserialize rejects it as "invalid arguments" up in the base.
        int? start = null, end = null;
        if (args.Range is { Count: 2 })
        {
            start = args.Range[0];
            end = args.Range[1];
        }

        await using var repo = await _databases
            .OpenAsync(_user.Iss, _user.Sub, invocation.Pid, cancellationToken)
            .ConfigureAwait(false);

        var artifact = await repo.GetArtifactByNameAsync(invocation.ConversationId, args.Name, cancellationToken)
            .ConfigureAwait(false);
        if (artifact is null)
        {
            return ToolCallResult<ReadArtifactResult>.Fail($"no artifact named '{args.Name}'.");
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

        var payload = new ReadArtifactResult
        {
            Name = artifact.Name,
            Format = ArtifactFormat.FromKind(artifact.Kind),
            Version = artifact.Version,
            LineCount = lines.Length,
            Content = sb.ToString(),
        };

        return ToolCallResult<ReadArtifactResult>.Ok(payload);
    }
}
