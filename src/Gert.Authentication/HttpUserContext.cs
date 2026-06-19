using System.Security.Claims;
using Gert.Service;
using Gert.Service.Tools;
using Microsoft.AspNetCore.Http;

namespace Gert.Authentication;

/// <summary>
/// <see cref="IUserContext"/> for the HTTP host: resolves the caller's scope from
/// the validated JWT on the current request (auth.md section the user context). All claims
/// come from <see cref="HttpContext.User"/> via an injected <see cref="IHttpContextAccessor"/>;
/// the service layer below never sees an <c>HttpContext</c> or a token.
/// </summary>
public sealed class HttpUserContext(
    IHttpContextAccessor http,
    ToolRegistry registry) : IUserContext
{
    private ClaimsPrincipal User =>
        http.HttpContext?.User
        ?? throw new UnauthorizedAccessException("no HTTP context");

    /// <inheritdoc />
    public string Sub =>
        User.FindFirstValue("sub")
        ?? throw new UnauthorizedAccessException("no sub claim");

    /// <inheritdoc />
    public string Iss =>
        User.FindFirstValue("iss")
        ?? throw new UnauthorizedAccessException("no iss claim");

    /// <inheritdoc />
    public string Username => User.Identity?.Name ?? Sub;

    /// <inheritdoc />
    public bool IsAdmin => User.IsInRole(GertJwtAuthExtensions.AdminRole);

    /// <inheritdoc />
    public IReadOnlySet<string> AllowedTools
    {
        get
        {
            var raw = User.FindFirstValue("gert_tools");

            // The JWT is the SOLE source of tool entitlement - there is no
            // server-side default grant (auth.md section tool entitlements). A token
            // that carries no gert_tools claim is granted NO tools: fail-closed,
            // every capability must be granted explicitly in the IdP.
            if (string.IsNullOrWhiteSpace(raw))
            {
                return EmptyTools;
            }

            // Blanket grant - every tool in the registry, current and future.
            if (raw.Trim() == "*")
            {
                return registry.AllIds;
            }

            // Canonical format: a space/comma-delimited scope string (OAuth-scope
            // style); Normalize intersects with the registry, dropping unknown ids.
            return registry.Normalize(raw);
        }
    }

    private static readonly IReadOnlySet<string> EmptyTools =
        new HashSet<string>(StringComparer.Ordinal);

    /// <inheritdoc />
    public bool CanUseTool(string id) => AllowedTools.Contains(id);
}
