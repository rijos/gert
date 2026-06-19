using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Serilog.Events;
using Serilog.Formatting;

namespace Gert.Service.Observability;

/// <summary>
/// The shared Gert NDJSON log formatter (operations.md section "Logging format (shared)"):
/// one JSON object per line to stdout, with <c>ts</c> then <c>level</c> <b>first</b>,
/// then <c>msg</c>, <c>comp</c>, and any contextual fields (<c>req</c>, <c>uid</c>,
/// <c>dur_ms</c>, ...). A single parser handles every .NET and Python process in the
/// deployment, so the field order is part of the contract - emitted explicitly here.
///
/// <list type="bullet">
///   <item><c>ts</c> - ISO-8601 <b>UTC</b>, millisecond precision (1st).</item>
///   <item><c>level</c> - lowercase <c>debug|info|warn|error</c> (2nd).</item>
///   <item><c>msg</c> - the rendered message.</item>
///   <item><c>comp</c> - component/category, from the <c>comp</c> property or
///         Serilog's <c>SourceContext</c>.</item>
///   <item>Then every remaining log property (<c>req</c>, <c>uid</c>, <c>dur_ms</c>, ...)
///         in event order.</item>
/// </list>
///
/// <para>
/// <b>Security (never logged):</b> this formatter only emits the structured properties
/// present on the event; the application is responsible for never putting raw tokens,
/// raw <c>sub</c>, email, or message/document content into a log property in the first
/// place. A user is identified only by the <c>uid</c> hash. The formatter adds nothing
/// of its own beyond <c>ts</c>/<c>level</c>/<c>msg</c>/<c>comp</c>.
/// </para>
/// </summary>
public sealed class GertNdjsonFormatter : ITextFormatter
{
    /// <summary>Property names handled positionally so they are never re-emitted in the tail.</summary>
    private static readonly HashSet<string> Reserved =
        new(StringComparer.Ordinal) { "comp", "SourceContext" };

    /// <inheritdoc />
    public void Format(LogEvent logEvent, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(output);

        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            // ts/level/msg/comp emitted in this fixed order: the field order is part
            // of the shared NDJSON contract (see class doc).
            writer.WriteString(
                "ts",
                logEvent.Timestamp.UtcDateTime.ToString(
                    "yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));

            writer.WriteString("level", MapLevel(logEvent.Level));

            writer.WriteString("msg", logEvent.RenderMessage(CultureInfo.InvariantCulture));

            // explicit `comp` property wins; else Serilog's SourceContext.
            var comp = ResolveComp(logEvent);
            if (comp is not null)
            {
                writer.WriteString("comp", comp);
            }

            // Tail - every remaining contextual property in event order.
            foreach (var (name, value) in logEvent.Properties)
            {
                if (Reserved.Contains(name))
                {
                    continue;
                }

                WriteProperty(writer, name, value);
            }

            // Exceptions are operational signal, not content - include the type+message.
            if (logEvent.Exception is { } ex)
            {
                writer.WriteString("err", $"{ex.GetType().Name}: {ex.Message}");
            }

            writer.WriteEndObject();
        }

        output.Write(Encoding.UTF8.GetString(buffer.WrittenSpan));
        output.Write('\n');
    }

    private static string MapLevel(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => "debug",
        LogEventLevel.Debug => "debug",
        LogEventLevel.Information => "info",
        LogEventLevel.Warning => "warn",
        LogEventLevel.Error => "error",
        LogEventLevel.Fatal => "error",
        _ => "info",
    };

    private static string? ResolveComp(LogEvent logEvent)
    {
        if (logEvent.Properties.TryGetValue("comp", out var comp) &&
            comp is ScalarValue { Value: { } compValue })
        {
            return compValue.ToString();
        }

        if (logEvent.Properties.TryGetValue("SourceContext", out var src) &&
            src is ScalarValue { Value: { } srcValue })
        {
            return srcValue.ToString();
        }

        return null;
    }

    private static void WriteProperty(Utf8JsonWriter writer, string name, LogEventPropertyValue value)
    {
        writer.WritePropertyName(name);
        WriteValue(writer, value);
    }

    private static void WriteValue(Utf8JsonWriter writer, LogEventPropertyValue value)
    {
        switch (value)
        {
            case ScalarValue scalar:
                WriteScalar(writer, scalar.Value);
                break;
            case SequenceValue seq:
                writer.WriteStartArray();
                foreach (var element in seq.Elements)
                {
                    WriteValue(writer, element);
                }

                writer.WriteEndArray();
                break;
            case StructureValue structure:
                writer.WriteStartObject();
                foreach (var prop in structure.Properties)
                {
                    WriteProperty(writer, prop.Name, prop.Value);
                }

                writer.WriteEndObject();
                break;
            case DictionaryValue dict:
                writer.WriteStartObject();
                foreach (var kvp in dict.Elements)
                {
                    var key = kvp.Key.Value?.ToString() ?? "null";
                    WriteProperty(writer, key, kvp.Value);
                }

                writer.WriteEndObject();
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    private static void WriteScalar(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case decimal m:
                writer.WriteNumberValue(m);
                break;
            case float f:
                writer.WriteNumberValue(f);
                break;
            default:
                writer.WriteStringValue(
                    Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                break;
        }
    }
}
