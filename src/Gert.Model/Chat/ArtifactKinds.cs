namespace Gert.Model.Chat;

/// <summary>
/// The <see cref="ArtifactKind"/> &lt;-&gt; on-disk token mapping (storage-and-data.md section
/// chat.db). The tokens (md/html/svg/py/cs/cpp/js/rs) are the persisted form in
/// <c>chat_objects.kind</c> and the kind tag carried on a stored object. Shared so the
/// repository and the chat-object resource never drift; both mappers throw on an unmapped
/// value, so a future kind missing from either fails loudly rather than at runtime.
/// </summary>
public static class ArtifactKinds
{
    /// <summary>The on-disk token for a kind.</summary>
    public static string ToToken(ArtifactKind kind) => kind switch
    {
        ArtifactKind.Md => "md",
        ArtifactKind.Html => "html",
        ArtifactKind.Svg => "svg",
        ArtifactKind.Py => "py",
        ArtifactKind.Cs => "cs",
        ArtifactKind.Cpp => "cpp",
        ArtifactKind.Js => "js",
        ArtifactKind.Rs => "rs",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>The kind for an on-disk token.</summary>
    public static ArtifactKind FromToken(string token) => token switch
    {
        "md" => ArtifactKind.Md,
        "html" => ArtifactKind.Html,
        "svg" => ArtifactKind.Svg,
        "py" => ArtifactKind.Py,
        "cs" => ArtifactKind.Cs,
        "cpp" => ArtifactKind.Cpp,
        "js" => ArtifactKind.Js,
        "rs" => ArtifactKind.Rs,
        _ => throw new InvalidOperationException($"Unknown artifact kind '{token}'."),
    };
}
