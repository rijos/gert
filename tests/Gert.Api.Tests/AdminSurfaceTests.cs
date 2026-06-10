using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Gert.Model.Json;
using Gert.Model.Projects;
using Gert.Service.Admin;
using Gert.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Gert.Api.Tests;

/// <summary>
/// The admin surface gate: RBAC (admin → 200) and the F6 <c>{key}</c> traversal
/// guard. Each test boots its own <see cref="GertApiFactory"/> with a singleton
/// <see cref="FakeAdminService"/> swapped in (the real service is still a U7c stub),
/// so the auth + validation behaviour is exercised end-to-end while the service's
/// folder-scan internals stay out of scope.
/// </summary>
public sealed class AdminSurfaceTests
{
    private static readonly JsonSerializerOptions Json = GertJsonOptions.Default;

    private static GertApiFactory WithFakeAdmin(FakeAdminService fake)
    {
        var factory = new GertApiFactory();
        factory.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAdminService>();
            services.AddSingleton<IAdminService>(fake);
        });
        return factory;
    }

    private static HttpClient AuthedAdmin(GertApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.TokenFor("admin"));
        return client;
    }

    [Fact]
    public async Task Admin_lists_users_200()
    {
        var fake = new FakeAdminService();
        using var factory = WithFakeAdmin(fake);
        var client = AuthedAdmin(factory);

        var users = await client.GetFromJsonAsync<IReadOnlyList<UserSummary>>("/api/admin/users", Json);

        users.Should().NotBeNull();
        users!.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("not-hex")]
    [InlineData("AABBCCDD")] // too short + uppercase
    public async Task Delete_with_routable_malformed_key_is_400_and_never_reaches_the_service(string badKey)
    {
        var fake = new FakeAdminService();
        using var factory = WithFakeAdmin(fake);

        // Sentinel dir under the temp DataRoot — assert it is never removed by a bad key.
        var sentinel = Path.Combine(factory.UsersDir, "sentinel");
        Directory.CreateDirectory(sentinel);

        var client = AuthedAdmin(factory);

        var response = await client.DeleteAsync($"/api/admin/users/{badKey}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        fake.SeenKeys.Should().BeEmpty("the F6 guard rejects the key before the service is called");
        Directory.Exists(sentinel).Should().BeTrue("no out-of-tree deletion happened");
    }

    [Theory]
    [InlineData("..")]
    [InlineData("%2e%2e%2f%2e%2e")]
    [InlineData("%2Fetc%2Fpasswd")] // absolute path, encoded
    [InlineData("%2e%2e%2fusers")]
    public async Task Delete_with_traversal_key_is_rejected_and_never_escapes_the_tree(string badKey)
    {
        var fake = new FakeAdminService();
        using var factory = WithFakeAdmin(fake);

        var sentinel = Path.Combine(factory.UsersDir, "sentinel");
        Directory.CreateDirectory(sentinel);

        var client = AuthedAdmin(factory);

        var response = await client.DeleteAsync($"/api/admin/users/{badKey}");

        // The controller guard rejects it (400) or URL normalization collapses it to a
        // non-matching route (404). Never a successful delete; never a path-join.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.NotFound);
        fake.SeenKeys.Should().BeEmpty("a traversal key never reaches the admin service");
        Directory.Exists(sentinel).Should().BeTrue("no out-of-tree deletion happened");
    }

    [Fact]
    public async Task Non_admin_delete_user_is_403_and_never_reaches_the_service()
    {
        var fake = new FakeAdminService();
        using var factory = WithFakeAdmin(fake);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.TokenFor("user"));

        var key = new string('a', 64); // well-formed, so only RBAC stands between us and the delete
        var response = await client.DeleteAsync($"/api/admin/users/{key}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        fake.SeenKeys.Should().BeEmpty("the admin policy rejects a non-admin before the service is called");
    }

    [Fact]
    public async Task Delete_with_well_formed_key_reaches_the_service()
    {
        var fake = new FakeAdminService();
        using var factory = WithFakeAdmin(fake);
        var client = AuthedAdmin(factory);

        var key = new string('a', 64); // matches ^[0-9a-f]{64}$
        var response = await client.DeleteAsync($"/api/admin/users/{key}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        fake.SeenKeys.Should().ContainSingle().Which.Should().Be(key);
    }
}
