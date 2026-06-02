using System.Security.Claims;
using System.Text.Json;
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

            // JSON array (["rag","search"]) or space/comma/tab-delimited string; either
            // way Normalize intersects with the registry and drops unknown ids.
            return registry.Normalize(ParseToolIds(raw));
        }
    }

    /// <inheritdoc />
    public bool CanUseTool(string id) => AllowedTools.Contains(id);

    /// <summary>
    /// Split a raw <c>gert_tools</c> value into candidate ids. A value that parses as a
    /// JSON string array is read element-wise; anything else is treated as a
    /// space/comma/tab-delimited list (handled by <see cref="ToolRegistry.Normalize(string)"/>).
    /// </summary>
    private static IEnumerable<string> ParseToolIds(string raw)
    {
        var trimmed = raw.TrimStart();
        if (trimmed.StartsWith('['))
        {
            string[]? parsed = null;
            try
            {
                parsed = JsonSerializer.Deserialize<string[]>(raw);
            }
            catch (JsonException)
            {
                // Not valid JSON after all — fall through to delimited parsing.
            }

            if (parsed is not null)
            {
                return parsed.Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id.Trim());
            }
        }

        return raw.Split(
            [' ', ',', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
