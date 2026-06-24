using Gert.Api.Contracts;
using Gert.Service;
using Gert.Tools;
using Microsoft.AspNetCore.Mvc;

namespace Gert.Api.Controllers;

/// <summary>
/// <c>GET /api/tools</c> - the tools this caller is entitled to (rest-api.md
/// section tools), the catalog that drives the composer's tools popup. The
/// registered <see cref="ITool"/> instances are filtered by the SAME hard
/// ceiling the turn planner applies (<see cref="IUserContext.CanUseTool"/> over
/// the <c>gert_tools</c> claim), so the popup and the turn agree: a tool the
/// model would be denied never even renders a row. The user is implicit (token);
/// nothing here is request-supplied. Covered by the fallback authenticated-user
/// policy; per-user, so <c>NoStoreByDefaultFilter</c> keeps it out of caches.
/// </summary>
[ApiController]
[Route("api/tools")]
public sealed class ToolsController : ControllerBase
{
    private readonly IEnumerable<ITool> _tools;
    private readonly IUserContext _user;

    public ToolsController(IEnumerable<ITool> tools, IUserContext user)
    {
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _user = user ?? throw new ArgumentNullException(nameof(user));
    }

    /// <summary>
    /// List the entitled tools (the full display descriptor: id/name/description/tool_type +
    /// title/icon/group/source/requires_human), ordered by id. An icon the client can't render
    /// (a tool naming a key outside <see cref="ToolIcons.Keys"/> - a future MCP tool, say)
    /// degrades to <see cref="ToolIcons.Fallback"/>, so the wire only carries renderable glyphs.
    /// </summary>
    [HttpGet]
    public ActionResult<IReadOnlyList<ToolInfo>> List() =>
        Ok(_tools
            .Where(tool => _user.CanUseTool(tool.Id))
            .OrderBy(tool => tool.Id, StringComparer.Ordinal)
            .Select(tool => new ToolInfo
            {
                Id = tool.Id,
                Name = tool.Name,
                Description = tool.Description,
                ToolType = tool.Type,
                Title = tool.Title,
                Icon = ToolIcons.Keys.Contains(tool.Icon) ? tool.Icon : ToolIcons.Fallback,
                Group = tool.Group,
                Source = "builtin",
                RequiresHuman = tool.RequiresHuman,
            })
            .ToList());
}
