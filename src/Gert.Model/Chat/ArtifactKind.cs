namespace Gert.Model.Chat;

/// <summary>
/// Canvas-tab artifact kind - mirrors <c>chat.db</c> <c>artifacts.kind</c>
/// (storage-and-data.md section chat.db).
/// </summary>
public enum ArtifactKind
{
    Md,
    Html,
    Svg,
    Py,
    Cs,
    Cpp,
    Js,
    Rs,
}
