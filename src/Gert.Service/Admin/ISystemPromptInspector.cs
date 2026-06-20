using Gert.Service.Chat;
using Gert.Tools;
using Microsoft.Extensions.Options;

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
    private readonly PromptOptions _prompts;

    public SystemPromptInspector(IEnumerable<ITool> tools, IOptions<PromptOptions> prompts)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _tools = tools.ToList();
        _prompts = prompts?.Value ?? throw new ArgumentNullException(nameof(prompts));
    }

    /// <inheritdoc />
    public SystemPromptSnapshot GetSnapshot() => new()
    {
        SystemPrompt = _prompts.Canvas,
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
