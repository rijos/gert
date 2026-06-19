using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Gert.Model.Json;
using Gert.Service.Admin;
using Gert.Testing;
using Xunit;

namespace Gert.Api.Tests;

/// <summary>
/// The admin model-prompt inspector (rest-api.md section admin): RBAC gating and the
/// snapshot shape - the system prompt plus every registered tool spec, exactly
/// as the request builder advertises them.
/// </summary>
public sealed class AdminSystemPromptTests
{
    private static readonly JsonSerializerOptions Json = GertJsonOptions.Default;

    private static HttpClient Client(GertApiFactory factory, string role)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.TokenFor(role));
        return client;
    }

    [Fact]
    public async Task Admin_gets_system_prompt_with_all_registered_tools()
    {
        using var factory = new GertApiFactory();
        var client = Client(factory, "admin");

        var snapshot = await client.GetFromJsonAsync<SystemPromptSnapshot>("/api/admin/system-prompt", Json);

        snapshot.Should().NotBeNull();
        snapshot!.SystemPrompt.Should().NotBeNullOrWhiteSpace("every turn carries the built-in prompt");
        snapshot.Tools.Should().NotBeEmpty();

        snapshot.Tools.Select(t => t.Name).Should().Contain(["web_search", "make_artifact", "run_python"]);
        snapshot.Tools.Should().OnlyContain(t =>
            !string.IsNullOrWhiteSpace(t.Id)
            && !string.IsNullOrWhiteSpace(t.Name)
            && !string.IsNullOrWhiteSpace(t.Description)
            && !string.IsNullOrWhiteSpace(t.ParametersSchema));
    }

    [Fact]
    public async Task Non_admin_is_403()
    {
        using var factory = new GertApiFactory();
        var client = Client(factory, "user");

        var response = await client.GetAsync("/api/admin/system-prompt");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Anonymous_is_401()
    {
        using var factory = new GertApiFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/admin/system-prompt");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
