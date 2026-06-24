using FluentAssertions;
using FluentValidation;
using FluentValidation.TestHelper;
using Gert.Model.Chat;
using Gert.Validation.Rules;
using Xunit;

namespace Gert.Validation.Tests;

/// <summary>
/// The shared <c>model_id</c> rule (<see cref="ValidationRules.ModelId{T}"/> /
/// <see cref="ValidationRules.IsKnownModelId"/>): a safe-token shape check plus an allow-list
/// against the configured providers, so an unknown slug 400s at the boundary instead of silently
/// resolving to the default. Permissive when no catalog is wired (zero-config dev).
/// </summary>
public sealed class ModelIdRuleTests
{
    private sealed class FakeModelIdCatalog : IModelIdCatalog
    {
        public FakeModelIdCatalog(params string[] ids) =>
            ModelIds = new HashSet<string>(ids, StringComparer.Ordinal);

        public IReadOnlySet<string> ModelIds { get; }
    }

    private sealed record Holder(string? ModelId);

    private sealed class HolderValidator : AbstractValidator<Holder>
    {
        public HolderValidator(IModelIdCatalog? catalog) =>
            RuleFor(h => h.ModelId!).ModelId(catalog).When(h => h.ModelId is not null);
    }

    [Fact]
    public void Null_model_id_is_allowed_meaning_use_the_default()
    {
        var v = new HolderValidator(new FakeModelIdCatalog("alpha", "beta"));
        v.TestValidate(new Holder(null)).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void A_configured_slug_passes()
    {
        var v = new HolderValidator(new FakeModelIdCatalog("alpha", "beta"));
        v.TestValidate(new Holder("beta")).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void The_default_sentinel_passes_even_when_not_a_listed_slug()
    {
        var v = new HolderValidator(new FakeModelIdCatalog("alpha", "beta"));
        v.TestValidate(new Holder(ChatProviderInfo.DefaultId)).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void An_unknown_slug_fails_with_the_unknown_code()
    {
        var v = new HolderValidator(new FakeModelIdCatalog("alpha", "beta"));
        v.TestValidate(new Holder("gamma"))
            .ShouldHaveValidationErrorFor(h => h.ModelId)
            .WithErrorCode("model_id.unknown");
    }

    [Fact]
    public void A_malformed_slug_fails_the_shape_check_first()
    {
        var v = new HolderValidator(new FakeModelIdCatalog("alpha"));
        v.TestValidate(new Holder("not a slug!"))
            .ShouldHaveValidationErrorFor(h => h.ModelId)
            .WithErrorCode("model_id.invalid");
    }

    [Fact]
    public void An_empty_catalog_is_permissive_zero_config_dev()
    {
        var v = new HolderValidator(new FakeModelIdCatalog());
        v.TestValidate(new Holder("anything-goes")).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void A_null_catalog_is_permissive_unwired()
    {
        var v = new HolderValidator(null);
        v.TestValidate(new Holder("anything-goes")).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("alpha", true)] // listed
    [InlineData("default", true)] // the sentinel
    [InlineData("zzz", false)] // unknown
    public void IsKnownModelId_checks_membership_when_the_catalog_is_configured(string id, bool expected)
    {
        var catalog = new FakeModelIdCatalog("alpha", "beta");
        ValidationRules.IsKnownModelId(catalog, id).Should().Be(expected);
    }

    [Fact]
    public void IsKnownModelId_is_permissive_without_a_catalog()
    {
        ValidationRules.IsKnownModelId(null, "whatever").Should().BeTrue();
        ValidationRules.IsKnownModelId(new FakeModelIdCatalog(), "whatever").Should().BeTrue();
    }
}
