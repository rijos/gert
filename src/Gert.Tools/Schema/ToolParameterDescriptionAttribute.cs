namespace Gert.Tools.Schema;

/// <summary>
/// The model-facing <c>"description"</c> for a tool-argument property; consumed by
/// <see cref="ToolSchema"/> when it generates a typed tool's parameter schema. Omitted
/// from the schema when absent (some properties carry only an enum).
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ToolParameterDescriptionAttribute : Attribute
{
    public ToolParameterDescriptionAttribute(string description) =>
        Description = description ?? throw new ArgumentNullException(nameof(description));

    /// <summary>The schema <c>"description"</c> text.</summary>
    public string Description { get; }
}
