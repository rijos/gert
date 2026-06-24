using System.Text.Json;
using FluentAssertions;
using Gert.Agent.Loop;
using Gert.Tools;
using Gert.Tools.Hosting;
using Microsoft.Extensions.AI;
using Xunit;

namespace Gert.Agent.Tests;

/// <summary>
/// The advertise-time <see cref="ToolFunction"/>: the tools region is a token budget (chat-and-tools.md
/// section tool specs - qwen format adherence collapses past ~1.8k tokens), so the advertised
/// <see cref="AIFunction"/> must carry the tool's OWN compact schema verbatim, never the verbose schema
/// <c>AIFunctionFactory.CreateDeclaration</c> would synthesise (which adds <c>additionalProperties</c>
/// and pretty-prints).
/// </summary>
public sealed class ToolFunctionTests
{
    [Fact]
    public void Advertised_tool_carries_the_tools_lean_schema_verbatim()
    {
        const string schema = """{"type":"object","properties":{"q":{"type":"string"}},"required":["q"]}""";
        var ids = new HashSet<string>(["t"], StringComparer.Ordinal);
        var set = new Toolset([new SchemaTool(schema)], ids, ids);

        var advertised = set.AdvertisedTools.Should().ContainSingle().Subject
            .Should().BeAssignableTo<AIFunction>().Subject;

        // Compact re-serialisation round-trips the tool's own schema with no added members.
        JsonSerializer.Serialize(advertised.JsonSchema).Should().Be(schema);
        advertised.Name.Should().Be("t_fn");
    }

    private sealed class SchemaTool(string schema) : ITool
    {
        public string Id => "t";

        public string Name => "t_fn";

        public string Description => "d";

        public string ParametersSchema => schema;

        public Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            IToolHost host,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolResult { Success = true });
    }
}
