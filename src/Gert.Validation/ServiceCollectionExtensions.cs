using FluentValidation;
using Gert.Model.Documents;
using Gert.Model.Dtos;
using Gert.Model.Projects;
using Gert.Tools;
using Gert.Validation.Validators;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gert.Validation;

/// <summary>
/// DI wiring for the fail-closed validation sub-layer (principles.md #6, security.md F6).
/// <see cref="AddGertServices"/>-equivalent for validation: the host-agnostic service layer's
/// <c>AddGertServices</c> calls <see cref="AddGertValidation"/> so a DTO with no registered
/// <c>IValidator&lt;T&gt;</c> makes the provider throw (the reflection meta-test keeps that throw
/// unreachable in production). The id-only <see cref="Gert.Tools.ToolRegistry"/> that
/// <see cref="Validators.ToolTogglesValidator"/> consumes is supplied separately by the
/// Gert.Tools.Builtin adapter (AddBuiltinTools); validation tests provide their own.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Wire the fail-closed validation provider and register every validator.
    /// Validators are registered <b>both</b> as their concrete type (so a parent
    /// validator can take a child via constructor injection for <c>SetValidator</c>)
    /// and as <c>IValidator&lt;T&gt;</c> (so <see cref="FluentValidationProvider"/>
    /// resolves them and the meta-test can discover them). Validators are stateless,
    /// so they are singletons.
    /// </summary>
    public static IServiceCollection AddGertValidation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<IValidationProvider, FluentValidationProvider>();

        // Shared / nested validators - needed by concrete type for SetValidator.
        AddValidator<ToolToggles, ToolTogglesValidator>(services);
        AddValidator<ProjectDefaults, ProjectDefaultsValidator>(services);
        AddValidator<Gert.Model.Chat.MessageAttachment, MessageAttachmentValidator>(services);

        // Request DTOs every service method accepts.
        AddValidator<SendMessageRequest, SendMessageRequestValidator>(services);
        AddValidator<CreateConversationRequest, CreateConversationRequestValidator>(services);
        AddValidator<UpdateConversationRequest, UpdateConversationRequestValidator>(services);
        AddValidator<MoveConversationRequest, MoveConversationRequestValidator>(services);
        AddValidator<CreateProjectRequest, CreateProjectRequestValidator>(services);
        AddValidator<UpdateProjectRequest, UpdateProjectRequestValidator>(services);
        AddValidator<CreateMemoryRequest, CreateMemoryRequestValidator>(services);
        AddValidator<UpdateSettingsRequest, UpdateSettingsRequestValidator>(services);
        AddValidator<DocumentUpload, DocumentUploadValidator>(services);
        AddValidator<AnswerRequest, AnswerRequestValidator>(services);

        // The Standard built-in tools' argument records (Gert.Tools/Args). Every
        // concrete ToolCall<TArgs, _> in Gert.Tools.Builtin needs a registered
        // IValidator<TArgs> - the base proves args fail-closed before CallAsync, and
        // FailClosedMetaTest's tool-args check keeps this list complete.
        AddValidator<RagArgs, RagArgsValidator>(services);
        AddValidator<WebSearchArgs, WebSearchArgsValidator>(services);
        AddValidator<WebFetchArgs, WebFetchArgsValidator>(services);
        AddValidator<PythonSandboxArgs, PythonSandboxArgsValidator>(services);
        AddValidator<ClockArgs, ClockArgsValidator>(services);
        AddValidator<TodoArgs, TodoArgsValidator>(services);
        AddValidator<MakeArtifactArgs, MakeArtifactArgsValidator>(services);
        AddValidator<EditArtifactArgs, EditArtifactArgsValidator>(services);
        AddValidator<ReadArtifactArgs, ReadArtifactArgsValidator>(services);
        AddValidator<SaveMemoryArgs, SaveMemoryArgsValidator>(services);

        return services;
    }

    /// <summary>
    /// Register <typeparamref name="TValidator"/> as both its concrete type and
    /// <c>IValidator&lt;TModel&gt;</c>, sharing one instance.
    /// </summary>
    private static void AddValidator<TModel, TValidator>(IServiceCollection services)
        where TValidator : class, IValidator<TModel>
    {
        services.TryAddSingleton<TValidator>();
        services.TryAddSingleton<IValidator<TModel>>(sp => sp.GetRequiredService<TValidator>());
    }
}
