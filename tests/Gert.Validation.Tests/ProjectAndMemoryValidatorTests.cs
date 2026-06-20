using FluentValidation;
using FluentValidation.TestHelper;
using Gert.Model.Dtos;
using Gert.Model.Projects;
using Gert.Validation;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gert.Validation.Tests;

/// <summary>
/// Positive / negative / boundary tests for the project / memory / settings /
/// conversation validators (testing.md section 5), against the production registration.
/// </summary>
public sealed class ProjectAndMemoryValidatorTests
{
    private readonly ServiceProvider _sp = ValidationTestHost.Build("rag", "search", "sandbox");

    [Fact]
    public void CreateProject_requires_a_safe_name()
    {
        var v = _sp.Validator<CreateProjectRequest>();
        v.TestValidate(new CreateProjectRequest { Name = "Research" }).ShouldNotHaveAnyValidationErrors();
        v.TestValidate(new CreateProjectRequest { Name = "  " }).ShouldHaveValidationErrorFor(r => r.Name);
        v.TestValidate(new CreateProjectRequest { Name = "ok" + (char)0x202E })
            .ShouldHaveValidationErrorFor(r => r.Name);
    }

    [Fact]
    public void CreateProject_instructions_boundary()
    {
        var v = _sp.Validator<CreateProjectRequest>();
        v.TestValidate(new CreateProjectRequest
        {
            Name = "ok",
            Instructions = new string('a', ValidationRules.MediumTextMax),
        }).ShouldNotHaveValidationErrorFor(r => r.Instructions);

        v.TestValidate(new CreateProjectRequest
        {
            Name = "ok",
            Instructions = new string('a', ValidationRules.MediumTextMax + 1),
        }).ShouldHaveValidationErrorFor(r => r.Instructions);
    }

    [Fact]
    public void UpdateProject_allows_all_unset_but_rejects_explicit_empty_name()
    {
        var v = _sp.Validator<UpdateProjectRequest>();
        v.TestValidate(new UpdateProjectRequest()).ShouldNotHaveAnyValidationErrors();
        v.TestValidate(new UpdateProjectRequest { Name = " " }).ShouldHaveValidationErrorFor(r => r.Name);
    }

    [Fact]
    public void CreateMemory_requires_safe_title_and_content()
    {
        var v = _sp.Validator<CreateMemoryRequest>();
        v.TestValidate(new CreateMemoryRequest { Title = "T", Content = "Body" })
            .ShouldNotHaveAnyValidationErrors();
        v.TestValidate(new CreateMemoryRequest { Title = string.Empty, Content = "Body" })
            .ShouldHaveValidationErrorFor(r => r.Title);
        v.TestValidate(new CreateMemoryRequest { Title = "T", Content = "  " })
            .ShouldHaveValidationErrorFor(r => r.Content);
    }

    [Fact]
    public void UpdateSettings_language_and_model_must_be_safe_tokens()
    {
        var v = _sp.Validator<UpdateSettingsRequest>();
        v.TestValidate(new UpdateSettingsRequest()).ShouldNotHaveAnyValidationErrors();
        v.TestValidate(new UpdateSettingsRequest { UiLanguage = "en-GB" })
            .ShouldNotHaveValidationErrorFor(r => r.UiLanguage);
        v.TestValidate(new UpdateSettingsRequest { ReplyLanguage = "has space" })
            .ShouldHaveValidationErrorFor(r => r.ReplyLanguage);
        v.TestValidate(new UpdateSettingsRequest { DefaultModelId = "bad id!" })
            .ShouldHaveValidationErrorFor(r => r.DefaultModelId);
    }

    [Fact]
    public void CreateConversation_optional_title_and_params()
    {
        var v = _sp.Validator<CreateConversationRequest>();
        v.TestValidate(new CreateConversationRequest()).ShouldNotHaveAnyValidationErrors();
        v.TestValidate(new CreateConversationRequest { Title = "  " })
            .ShouldHaveValidationErrorFor(r => r.Title);
    }

    [Fact]
    public void ProjectDefaults_nested_model_and_tools_validated()
    {
        var v = _sp.Validator<ProjectDefaults>();
        v.TestValidate(new ProjectDefaults { ModelId = "qwen2.5:7b" }).ShouldNotHaveAnyValidationErrors();
        v.TestValidate(new ProjectDefaults { ModelId = "bad id" })
            .ShouldHaveValidationErrorFor(d => d.ModelId);
    }
}
