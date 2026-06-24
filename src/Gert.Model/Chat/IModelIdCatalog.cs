namespace Gert.Model.Chat;

/// <summary>
/// The set of configured model/provider ids a request's <c>model_id</c> is allow-listed against
/// (rest-api.md section models). A focused, secret-free port in <c>Gert.Model</c> so the
/// validation layer can reject an unknown slug at the boundary without an inward edge to
/// <c>Gert.Chat</c>; the chat catalog implements it. An EMPTY set means no catalog is wired (the
/// zero-config dev default), in which case model_id validation is permissive - there is nothing
/// to check against.
/// </summary>
public interface IModelIdCatalog
{
    /// <summary>The configured provider slugs (<see cref="ChatProviderInfo.Id"/>); empty = permissive.</summary>
    IReadOnlySet<string> ModelIds { get; }
}
