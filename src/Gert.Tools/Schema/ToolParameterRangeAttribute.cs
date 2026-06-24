namespace Gert.Tools.Schema;

/// <summary>
/// The inclusive <c>"minimum"</c>/<c>"maximum"</c> bounds for an integer tool-argument
/// property; rendered into the schema by <see cref="ToolSchema"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ToolParameterRangeAttribute : Attribute
{
    public ToolParameterRangeAttribute(int minimum, int maximum)
    {
        Minimum = minimum;
        Maximum = maximum;
    }

    /// <summary>The schema <c>"minimum"</c>.</summary>
    public int Minimum { get; }

    /// <summary>The schema <c>"maximum"</c>.</summary>
    public int Maximum { get; }
}
