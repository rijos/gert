namespace Gert.Tools.Schema;

/// <summary>
/// The closed value set for a string tool-argument property; rendered as the schema's
/// <c>"enum"</c> array by <see cref="ToolSchema"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ToolParameterEnumAttribute : Attribute
{
    public ToolParameterEnumAttribute(params string[] values) =>
        Values = values ?? throw new ArgumentNullException(nameof(values));

    /// <summary>The allowed values, in schema order.</summary>
    public IReadOnlyList<string> Values { get; }
}
