using FluentAssertions;
using FluentValidation;
using Gert.Model.Documents;
using Gert.Service;
using Gert.Tools.Builtin;
using Gert.Validation;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gert.Validation.Tests;

/// <summary>
/// The keystone (testing.md section 5 #3; principle #6 in executable form). Since the
/// move to proof types, the contract is enforced two ways the older
/// "is a validator registered?" check could not:
/// <list type="number">
///   <item><b>No service method accepts a raw request DTO.</b> Every request DTO a
///   host hands a service crosses the boundary as <see cref="Validated{T}"/>, never the
///   bare POCO - so validation cannot be silently forgotten in a method body, because
///   the method cannot be <i>written</i> to take an unvalidated value.</item>
///   <item><b>Every wrapped DTO still has a registered validator.</b> A
///   <c>Validated&lt;T&gt;</c> whose <c>T</c> has no <c>IValidator&lt;T&gt;</c> would
///   throw at proof construction - fail-closed - so the registration check rides along
///   on the unwrapped inner type.</item>
/// </list>
///
/// <para><b>Discovery strategy.</b> The single, authoritative list of "the services a
/// host calls" is the <see cref="IGertServices"/> hub. We walk its property types (the
/// granular service interfaces) plus the two detached chat boundaries
/// (<see cref="Gert.Agent.ITurnPlanner"/>, <see cref="Gert.Agent.ITurnQuestions"/>),
/// then every method parameter. Route-param strings (<c>pid</c>, admin <c>{key}</c>)
/// are not DTOs and are covered by <see cref="RouteParamValidationTests"/>.</para>
/// </summary>
public sealed class FailClosedMetaTest
{
    [Fact]
    public void No_service_method_accepts_a_raw_request_dto()
    {
        // The structural invariant: a request DTO must arrive as Validated<T>. Any
        // bare DTO parameter means a service that validates (or forgets to) inside its
        // own body - the exact pattern proof types replace.
        var raw = ServiceParameterTypes()
            .Where(IsRequestDto)
            .Select(t => t.FullName ?? t.Name)
            .Distinct()
            .ToList();

        raw.Should().BeEmpty(
            "every request DTO a service accepts must cross the boundary as " +
            "Validated<T>, not a raw POCO (principle #6). Raw: " +
            string.Join(", ", raw));
    }

    [Fact]
    public void Every_validated_request_dto_has_a_registered_validator()
    {
        var sp = ValidationTestHost.Build("rag", "search", "sandbox");
        var wrapped = WrappedRequestDtoTypes();

        wrapped.Should().NotBeEmpty("the discovery must find the wrapped service request DTOs");

        var missing = new List<string>();
        foreach (var dto in wrapped)
        {
            var validatorType = typeof(IValidator<>).MakeGenericType(dto);
            if (sp.GetService(validatorType) is null)
            {
                missing.Add(dto.FullName ?? dto.Name);
            }
        }

        missing.Should().BeEmpty(
            "every DTO wrapped in Validated<T> at a service boundary must have a " +
            "registered IValidator<T> (else Validated<T>.From throws - fail-closed). " +
            "Missing: " + string.Join(", ", missing));
    }

    [Fact]
    public void Discovery_finds_the_known_request_dtos()
    {
        // Guards the discovery itself: if the filter ever stops seeing these, the
        // checks above could pass vacuously. These are the inputs we know cross the
        // boundary today - now as the inner type of a Validated<T> parameter.
        var names = WrappedRequestDtoTypes().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        names.Should().Contain(new[]
        {
            "SendMessageRequest",
            "CreateConversationRequest",
            "UpdateConversationRequest",
            "MoveConversationRequest",
            "CreateProjectRequest",
            "UpdateProjectRequest",
            "UpdateSettingsRequest",
            "DocumentUpload",
            "AnswerRequest",
        });
    }

    [Fact]
    public void Every_builtin_tool_call_has_a_registered_args_validator()
    {
        // The tool-side keystone (chat-and-tools.md section tool loop): the typed-args
        // base (ToolCall<TArgs, _>) proves a tool's arguments through IValidator<TArgs>
        // before CallAsync runs, so a TArgs with no registered validator would throw
        // fail-closed at the first model tool call. Reflect over every concrete
        // ToolCall<,> in the Gert.Tools.Builtin assembly and assert its TArgs resolves.
        var sp = ValidationTestHost.Build("rag", "search", "sandbox");
        var argTypes = ToolCallArgTypes();

        argTypes.Should().NotBeEmpty("the discovery must find the migrated typed-args tools");

        var missing = new List<string>();
        foreach (var args in argTypes)
        {
            var validatorType = typeof(IValidator<>).MakeGenericType(args);
            if (sp.GetService(validatorType) is null)
            {
                missing.Add(args.FullName ?? args.Name);
            }
        }

        missing.Should().BeEmpty(
            "every ToolCall<TArgs, _> in Gert.Tools.Builtin must have a registered " +
            "IValidator<TArgs> (else the base's Prove throws on the first call - " +
            "fail-closed). Missing: " + string.Join(", ", missing));
    }

    [Fact]
    public void Tool_args_discovery_finds_the_migrated_tools()
    {
        // Guards the discovery itself: if the filter ever stops seeing these, the check
        // above could pass vacuously. Covers the Standard typed-args tools AND the modal
        // ones - ask_user / sub_agent now derive from ToolCallModal<TArgs, _> : ToolCall<TArgs, _>,
        // so the base-chain walk discovers their args too (and must require validators for them).
        var names = ToolCallArgTypes().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        names.Should().Contain(new[]
        {
            "RagArgs",
            "ReadDocumentArgs",
            "WebSearchArgs",
            "WebFetchArgs",
            "PythonSandboxArgs",
            "ClockArgs",
            "TodoArgs",
            "MakeArtifactArgs",
            "EditArtifactArgs",
            "ReadArtifactArgs",
            "ListArtifactsArgs",
            "AskUserArgs",
            "SubAgentArgs",
        });
    }

    /// <summary>
    /// The <c>TArgs</c> of every concrete <c>ToolCall&lt;TArgs, _&gt;</c> in the
    /// Gert.Tools.Builtin assembly - walking each type's base chain for the closed
    /// generic so a tool that also implements a marker (e.g. <c>IToolReminder</c>) is
    /// still seen.
    /// </summary>
    private static IReadOnlyList<Type> ToolCallArgTypes() =>
        typeof(RagTool).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Select(ToolCallArgs)
            .Where(t => t is not null)
            .Select(t => t!)
            .Distinct()
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

    /// <summary>The <c>TArgs</c> of the closed <c>ToolCall&lt;,&gt;</c> in <paramref name="type"/>'s base chain, or null.</summary>
    private static Type? ToolCallArgs(Type type)
    {
        for (var t = type.BaseType; t is not null; t = t.BaseType)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ToolCall<,>))
            {
                return t.GetGenericArguments()[0];
            }
        }

        return null;
    }

    /// <summary>Every parameter type of every service-boundary method (proof types and all).</summary>
    private static IReadOnlyList<Type> ServiceParameterTypes()
    {
        var serviceInterfaces = typeof(IGertServices)
            .GetProperties()
            .Select(p => p.PropertyType)
            .Where(t => t.IsInterface)
            .ToList();

        // Chat is not on the hub (the detached turn pipeline injects its seams
        // directly - chat-and-tools.md section detached turns), but its planner and the
        // ask_user answer registry are still host-called boundaries that accept request
        // DTOs: include them explicitly so SendMessageRequest / AnswerRequest never drop
        // out of the net.
        serviceInterfaces.Add(typeof(Gert.Agent.ITurnPlanner));
        serviceInterfaces.Add(typeof(Gert.Agent.ITurnQuestions));

        return serviceInterfaces
            .SelectMany(svc => svc.GetMethods())
            .SelectMany(method => method.GetParameters())
            .Select(p => p.ParameterType)
            .ToList();
    }

    /// <summary>The inner <c>T</c> of every <c>Validated&lt;T&gt;</c> service parameter.</summary>
    private static IReadOnlyList<Type> WrappedRequestDtoTypes() =>
        ServiceParameterTypes()
            .Where(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Validated<>))
            .Select(t => t.GetGenericArguments()[0])
            .Distinct()
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

    private static bool IsRequestDto(Type t)
    {
        if (t.IsInterface || t.IsPrimitive || t.IsEnum || t.IsValueType)
        {
            return false;
        }

        if (t == typeof(string) || t == typeof(CancellationToken) || t == typeof(object))
        {
            return false;
        }

        // Only types we own - and not the streaming/event/result surface.
        var asm = t.Assembly.GetName().Name;
        if (asm != "Gert.Model" && asm != "Gert.Service")
        {
            return false;
        }

        var ns = t.Namespace ?? string.Empty;

        // Request DTOs live in the Dtos namespace; DocumentUpload is the one upload
        // input that lives beside its service. Exclude event/result/chat-turn types.
        var isDtoNamespace = ns.StartsWith("Gert.Model.Dtos", StringComparison.Ordinal);
        var isUploadInput = t.Name.EndsWith("Upload", StringComparison.Ordinal);

        return isDtoNamespace || isUploadInput;
    }
}
