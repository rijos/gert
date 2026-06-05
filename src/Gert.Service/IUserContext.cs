namespace Gert.Service;

/// <summary>
/// The current user's scope, resolved per request from the validated token
/// (auth.md § user context). Host-agnostic: <c>Gert.Api</c> implements it from
/// JWT claims (<c>HttpUserContext</c>), <c>Gert.Console</c> from a fixed local
/// user (<c>LocalUserContext</c>, tools = <c>*</c>). The service layer only ever
/// sees this abstraction — never an <c>HttpContext</c> or a JWT.
/// </summary>
public interface IUserContext
{
    /// <summary>Stable, never-recycled IdP subject id — the folder anchor (with <see cref="Iss"/>).</summary>
    string Sub { get; }

    /// <summary>Token issuer — combined with <see cref="Sub"/> to derive the user folder key.</summary>
    string Iss { get; }

    /// <summary>Human-readable display name (the userchip); falls back to <see cref="Sub"/>.</summary>
    string Username { get; }

    /// <summary>True when the user is in the admin role (<c>gert-admins</c>).</summary>
    bool IsAdmin { get; }

    /// <summary>
    /// The tool capability ids this user is granted — the hard ceiling on which
    /// tools the model may call (auth.md § entitlement). Derived from the
    /// <c>gert_tools</c> claim ∩ the registry, or the default grant.
    /// </summary>
    IReadOnlySet<string> AllowedTools { get; }

    /// <summary>True if <paramref name="id"/> is in <see cref="AllowedTools"/>.</summary>
    bool CanUseTool(string id);
}
