using System.Security.Claims;
using Gert.Service;
using Gert.Service.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Gert.Authentication;

/// <summary>
/// <see cref="IUserContext"/> for the HTTP host: resolves the caller's scope from
/// the validated JWT on the current request (auth.md § the user context). All claims
/// come from <see cref="HttpContext.User"/> via an injected <see cref="IHttpContextAccessor"/>;
/// the service layer below never sees an <c>HttpContext</c> or a token.
/// </summary>
public sealed class HttpUserContext(
    IHttpContextAccessor http,
    ToolRegistry registry,
    IOptions<ToolOptions> tools) : IUserContext
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
    public bool IsAdmin => User.IsInRole("gert-admins");

    /// <inheritdoc />
    public IReadOnlySet<string> AllowedTools
    {
        get
        {
            var raw = User.FindFirstValue("gert_tools");

            // Claim absent / blank → the configured default grant, ∩ registry so a
            // mis-configured default can never name a tool the system doesn't have.
            if (string.IsNullOrWhiteSpace(raw))
            {
                return registry.Normalize(tools.Value.DefaultGrant);
            }

            // Blanket grant — every tool in the registry, current and future.
            if (raw.Trim() == "*")
            {
                return registry.AllIds;
            }

            // Canonical format: a single space/comma-delimited scope string
            // (OAuth-scope style). Normalize splits it and intersects with the
            // registry, dropping any id the system doesn't have.
            return registry.Normalize(raw);
        }
    }

    /// <inheritdoc />
    public bool CanUseTool(string id) => AllowedTools.Contains(id);
}
