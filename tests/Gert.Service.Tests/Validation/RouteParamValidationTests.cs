using FluentAssertions;
using Gert.Service.Validation;
using Gert.Testing.TestData;
using Xunit;

namespace Gert.Service.Tests.Validation;

/// <summary>
/// Tests for the reusable route-parameter validators (security F6 admin
/// <c>{key}</c>; configuration.md section 2.5 <c>{pid}</c>) that the controllers
/// call before path-joining. They return the same
/// <see cref="ValidationResult"/> shape as the body validators.
/// </summary>
public sealed class RouteParamValidationTests
{
    [Fact]
    public void Valid_admin_key_passes()
    {
        var key = new string('a', 32) + new string('f', 32); // 64 hex chars
        RouteParamValidation.ValidateAdminKey(key).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("../../etc/passwd")]
    public void Malformed_admin_key_fails(string? key)
    {
        var result = RouteParamValidation.ValidateAdminKey(key);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Code == "admin_key.invalid" && e.Property == "key");
    }

    [Theory]
    [MemberData(nameof(TraversalRows))]
    public void Admin_key_rejects_every_traversal_payload(string payload) =>
        RouteParamValidation.ValidateAdminKey(payload).IsValid.Should().BeFalse();

    [Fact]
    public void Project_id_accepts_default_and_guid()
    {
        RouteParamValidation.ValidateProjectId("default").IsValid.Should().BeTrue();
        RouteParamValidation.ValidateProjectId(System.Guid.NewGuid().ToString()).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-guid")]
    [InlineData("../escape")]
    public void Malformed_project_id_fails(string? pid)
    {
        var result = RouteParamValidation.ValidateProjectId(pid);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Code == "pid.invalid" && e.Property == "pid");
    }

    [Theory]
    [MemberData(nameof(TraversalRows))]
    public void Project_id_rejects_every_traversal_payload(string payload) =>
        RouteParamValidation.ValidateProjectId(payload).IsValid.Should().BeFalse();

    public static IEnumerable<object[]> TraversalRows() =>
        NaughtyStrings.TheoryData(NaughtyStrings.PathTraversal);
}
