using Gert.Model;
using Gert.Service.External;
using Gert.External.Vllm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Gert.External;

/// <summary>
/// The operator model catalog (rest-api.md § models): the <c>Gert:Models</c>
/// configuration section, in configured order. When the section is absent or
/// empty, the single configured vLLM chat model (<c>Gert:Vllm:ChatModelId</c>)
/// is surfaced so the picker — and the default-model setting — always have one
/// real option. The same resolved list answers <see cref="SupportsTools"/>, so
/// the tool gate and the picker can never disagree.
/// </summary>
public sealed class ConfigModelCatalog : IModelCatalog
{
    /// <summary>The configuration section the catalog binds from.</summary>
    public const string SectionName = "Gert:Models";

    private readonly IReadOnlyList<ModelInfo> _models;

    /// <summary>Bind the catalog once — configuration is fixed for the host's lifetime.</summary>
    public ConfigModelCatalog(IConfiguration configuration, IOptions<VllmOptions> vllm)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(vllm);

        var configured = configuration.GetSection(SectionName).Get<List<ModelInfo>>();
        _models = configured is { Count: > 0 }
            ? configured
            : [Fallback(vllm.Value.ChatModelId)];
    }

    private static ModelInfo Fallback(string chatModelId) => new()
    {
        Id = chatModelId,
        Name = chatModelId == "default" ? "Default" : chatModelId,
        Default = true,
        // Permissive like an undeclared entry: assume capable rather than
        // silently cripple — a non-vision deployment errors the turn honestly.
        Capabilities = [ModelInfo.ToolsCapability, ModelInfo.VisionCapability],
        // The single-vLLM deployment this fallback exists for serves Qwen3.6,
        // whose generation_config.json carries only the thinking-mode sampling
        // — declare the card's instruct set so thinking-off turns don't decode
        // with the wrong mode's parameters (and repetition-loop without the
        // presence penalty). An operator who configures Gert:Models takes
        // over completely, this entry included.
        InstructParams = new Gert.Model.Dtos.GenerationParams
        {
            Temperature = 0.7,
            TopP = 0.8,
            PresencePenalty = 1.5,
        },
    };

    /// <inheritdoc />
    public IReadOnlyList<ModelInfo> List() => _models;

    /// <inheritdoc />
    public bool SupportsTools(string modelId) =>
        _models.FirstOrDefault(m => m.Id == modelId)?.SupportsTools ?? true;

    /// <inheritdoc />
    public bool SupportsVision(string modelId) =>
        _models.FirstOrDefault(m => m.Id == modelId)?.SupportsVision ?? true;

    /// <inheritdoc />
    public Gert.Model.Dtos.GenerationParams? InstructParams(string modelId) =>
        _models.FirstOrDefault(m => m.Id == modelId)?.InstructParams;
}
