using FluentAssertions;
using FluentValidation;
using Gert.Model.Dtos;
using Gert.Service.Validation;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gert.Service.Tests.Validation;

/// <summary>
/// The provider-contract tests (testing.md section 5 #4): the real
/// <see cref="FluentValidationProvider"/> resolves the right validator, surfaces a
/// consistent <see cref="ValidationResult"/> / <see cref="ValidationException"/>
/// shape, and — the keystone of fail-closed — <b>throws</b> for a type with no
/// registered validator rather than silently passing.
/// </summary>
public sealed class FluentValidationProviderTests
{
    [Fact]
    public void Resolves_the_registered_validator_and_returns_success_for_valid_input()
    {
        var sp = ValidationTestHost.Build("rag");
        var provider = sp.GetRequiredService<IValidationProvider>();

        var result = provider.Validate(new SendMessageRequest { Content = "hello" });

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Maps_failures_into_the_consistent_error_shape()
    {
        var sp = ValidationTestHost.Build("rag");
        var provider = sp.GetRequiredService<IValidationProvider>();

        var result = provider.Validate(new SendMessageRequest { Content = "   " });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
        result.Errors.Should().OnlyContain(e =>
            !string.IsNullOrEmpty(e.Property) && !string.IsNullOrEmpty(e.Message));
        // The provider preserves FluentValidation's error codes.
        result.Errors.Should().Contain(e => e.Code == "text.empty");
    }

    [Fact]
    public void Null_instance_is_a_failure_not_a_crash()
    {
        var sp = ValidationTestHost.Build("rag");
        var provider = sp.GetRequiredService<IValidationProvider>();

        var result = provider.Validate<SendMessageRequest>(null!);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "request.null");
    }

    [Fact]
    public void Fail_closed_throws_for_a_type_with_no_registered_validator()
    {
        var sp = ValidationTestHost.Build("rag");
        var provider = sp.GetRequiredService<IValidationProvider>();

        var act = () => provider.Validate(new UnregisteredDto());

        act.Should().Throw<ValidatorNotRegisteredException>()
            .Which.DtoType.Should().Be(typeof(UnregisteredDto));
    }

    [Fact]
    public void ValidationException_carries_the_result_for_the_host_to_render()
    {
        var sp = ValidationTestHost.Build("rag");
        var provider = sp.GetRequiredService<IValidationProvider>();
        var result = provider.Validate(new SendMessageRequest { Content = string.Empty });

        var ex = new Gert.Service.Validation.ValidationException(result);

        ex.Result.Should().BeSameAs(result);
        ex.Message.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>A throwaway DTO deliberately left without a validator.</summary>
    private sealed record UnregisteredDto;
}
