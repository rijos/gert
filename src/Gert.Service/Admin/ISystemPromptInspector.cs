using Gert.Service.Chat;
using Gert.Tools;

namespace Gert.Service.Admin;

/// <summary>
/// Read-only inspection of what the model is sent (rest-api.md section admin): the
/// stable system prompt and the full registered tool specs. Pure configuration
/// - no user data, so it is safe behind the admin policy.
/// </summary>
public interface ISystemPromptInspector
{
    SystemPromptSnapshot GetSnapshot();
}

/// <inheritdoc cref="ISystemPromptInspector"/>
public sealed class SystemPromptInspector : ISystemPromptInspector
{
    private readonly IReadOnlyList<ITool> _tools;

    public SystemPromptInspector(IEnumerable<ITool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _tools = tools.ToList();
    }

    /// <inheritdoc />
    public SystemPromptSnapshot GetSnapshot() => new()
    {
        SystemPrompt = SystemPrompts.Canvas,
        Tools = _tools
            .Select(t => new ToolSpecView
            {
                Id = t.Id,
                Name = t.Name,
                Description = t.Description,
                ParametersSchema = t.ParametersSchema,
            })
            .ToList(),
    };
}
