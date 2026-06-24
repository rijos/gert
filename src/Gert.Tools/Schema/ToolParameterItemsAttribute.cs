namespace Gert.Tools.Schema;

/// <summary>
/// The <c>"minItems"</c>/<c>"maxItems"</c> bounds for an array tool-argument property;
/// rendered into the schema by <see cref="ToolSchema"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ToolParameterItemsAttribute : Attribute
{
    public ToolParameterItemsAttribute(int minItems, int maxItems)
    {
        MinItems = minItems;
        MaxItems = maxItems;
    }

    /// <summary>The schema <c>"minItems"</c>.</summary>
    public int MinItems { get; }

    /// <summary>The schema <c>"maxItems"</c>.</summary>
    public int MaxItems { get; }
}
