using System.Text.Json;
using FluentAssertions;
using Gert.Service.Observability;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace Gert.Service.Tests.Observability;

/// <summary>
/// Verifies the shared NDJSON formatter (operations.md section Logging format): <c>ts</c> then
/// <c>level</c> are the first two properties, levels map to the lowercase vocabulary, and
/// nothing the app marked secret leaks - the formatter only emits the properties present.
/// </summary>
public sealed class GertNdjsonFormatterTests
{
    [Fact]
    public void Emits_ts_then_level_as_first_two_properties()
    {
        var line = Format(LogEventLevel.Information, "chat stream complete", ("comp", "chat"), ("dur_ms", 142));

        // Key order is part of the cross-process contract - assert on the raw string.
        var tsIndex = line.IndexOf("\"ts\":", StringComparison.Ordinal);
        var levelIndex = line.IndexOf("\"level\":", StringComparison.Ordinal);
        var msgIndex = line.IndexOf("\"msg\":", StringComparison.Ordinal);

        tsIndex.Should().Be(1); // immediately after the opening '{'
        levelIndex.Should().BeGreaterThan(tsIndex);
        msgIndex.Should().BeGreaterThan(levelIndex);

        // It is one valid JSON object terminated by a newline.
        line.Should().EndWith("\n");
        using var doc = JsonDocument.Parse(line);
        var props = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        props[0].Should().Be("ts");
        props[1].Should().Be("level");
        doc.RootElement.GetProperty("level").GetString().Should().Be("info");
        doc.RootElement.GetProperty("comp").GetString().Should().Be("chat");
        doc.RootElement.GetProperty("dur_ms").GetInt32().Should().Be(142);
    }

    [Theory]
    [InlineData(LogEventLevel.Verbose, "debug")]
    [InlineData(LogEventLevel.Debug, "debug")]
    [InlineData(LogEventLevel.Information, "info")]
    [InlineData(LogEventLevel.Warning, "warn")]
    [InlineData(LogEventLevel.Error, "error")]
    [InlineData(LogEventLevel.Fatal, "error")]
    public void Maps_levels_to_lowercase_vocabulary(LogEventLevel level, string expected)
    {
        var line = Format(level, "x");
        using var doc = JsonDocument.Parse(line);
        doc.RootElement.GetProperty("level").GetString().Should().Be(expected);
    }

    [Fact]
    public void Does_not_emit_secrets_not_placed_on_the_event()
    {
        // The formatter only serialises the event's properties. Simulate the contract:
        // the app logs a uid hash but NEVER a raw sub/token - so neither appears.
        const string rawSub = "8f14e45fceea167a5a36dedd4bea2543";
        const string token = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.secret";
        var uid = UserIdHash.Compute("https://idp.example", rawSub);

        var line = Format(LogEventLevel.Information, "request done", ("comp", "api"), ("uid", uid));

        line.Should().Contain(uid);
        line.Should().NotContain(rawSub);
        line.Should().NotContain(token);
        line.Should().NotContain("Bearer");
    }

    [Fact]
    public void UserIdHash_is_a_short_prefix_and_not_the_raw_sub()
    {
        const string sub = "subject-12345";
        var uid = UserIdHash.Compute("https://idp.example", sub);

        uid.Should().HaveLength(UserIdHash.PrefixLength);
        uid.Should().NotContain(sub);
        uid.Should().MatchRegex("^[0-9a-f]+$");

        // Deterministic and input-sensitive.
        UserIdHash.Compute("https://idp.example", sub).Should().Be(uid);
        UserIdHash.Compute("https://idp.example", "other").Should().NotBe(uid);
    }

    private static string Format(LogEventLevel level, string message, params (string Name, object Value)[] props)
    {
        var parser = new MessageTemplateParser();
        var template = parser.Parse(message);
        var properties = props.Select(p =>
            new LogEventProperty(p.Name, new ScalarValue(p.Value)));

        var logEvent = new LogEvent(
            new DateTimeOffset(2026, 6, 2, 12, 34, 56, 789, TimeSpan.Zero),
            level,
            exception: null,
            template,
            properties);

        using var writer = new StringWriter();
        new GertNdjsonFormatter().Format(logEvent, writer);
        return writer.ToString();
    }
}
