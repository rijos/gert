using System.Reflection;
using FluentAssertions;
using FluentValidation;
using Gert.Service;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gert.Service.Tests.Validation;

/// <summary>
/// The keystone (testing.md section 5 #3; principle #6 in executable form): a
/// reflection test asserting <b>every request DTO a service method accepts has a
/// registered <c>IValidator&lt;T&gt;</c></b>. If a new DTO ships without a
/// validator, this goes RED — validation can never be silently forgotten.
///
/// <para><b>Discovery strategy.</b> The single, authoritative list of "the
/// services a host calls" is the <see cref="IGertServices"/> hub. We walk its
/// property types (the granular service interfaces), then every method parameter
/// of those interfaces, and keep the parameters that are <i>request DTOs</i>: a
/// class/record (not an interface, primitive, string, enum, CancellationToken, or
/// a streaming/event type) declared in the Gert.Model or Gert.Service assemblies.
/// That set is exactly the inputs that cross the validation boundary
/// (SendMessageRequest, the Create/Update requests, CreateMemoryRequest,
/// DocumentUpload, ...). Route-param strings (<c>pid</c>, admin <c>{key}</c>) are
/// not DTOs and are covered by <see cref="RouteParamValidationTests"/>.</para>
/// </summary>
public sealed class FailClosedMetaTest
{
    [Fact]
    public void Every_request_dto_a_service_accepts_has_a_registered_validator()
    {
        var sp = ValidationTestHost.Build("rag", "search", "sandbox");
        var dtoTypes = DiscoverRequestDtoTypes();

        dtoTypes.Should().NotBeEmpty("the discovery must find the service request DTOs");

        var missing = new List<string>();
        foreach (var dto in dtoTypes)
        {
            var validatorType = typeof(IValidator<>).MakeGenericType(dto);
            if (sp.GetService(validatorType) is null)
            {
                missing.Add(dto.FullName ?? dto.Name);
            }
        }

        missing.Should().BeEmpty(
            "every request DTO a service accepts must have a registered IValidator<T> " +
            "(principle #6, fail-closed). Missing: " + string.Join(", ", missing));
    }

    [Fact]
    public void Discovery_finds_the_known_request_dtos()
    {
        // Guards the discovery itself: if the filter ever stops seeing these, the
        // meta-test above could pass vacuously. These are the inputs we know cross
        // the boundary today.
        var names = DiscoverRequestDtoTypes().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        names.Should().Contain(new[]
        {
            "SendMessageRequest",
            "CreateConversationRequest",
            "UpdateConversationRequest",
            "CreateProjectRequest",
            "UpdateProjectRequest",
            "CreateMemoryRequest",
            "UpdateSettingsRequest",
            "DocumentUpload",
            "AnswerRequest",
        });
    }

    private static IReadOnlyList<Type> DiscoverRequestDtoTypes()
    {
        var serviceInterfaces = typeof(IGertServices)
            .GetProperties()
            .Select(p => p.PropertyType)
            .Where(t => t.IsInterface)
            .ToList();

        // Chat is not on the hub (the detached turn pipeline injects its seams
        // directly — chat-and-tools.md § detached turns), but its planner is still
        // a host-called boundary that accepts a request DTO: include it explicitly
        // so SendMessageRequest never drops out of the fail-closed net.
        serviceInterfaces.Add(typeof(Gert.Service.Chat.ITurnPlanner));

        // Same for the ask_user answer registry: the controller hands it the
        // AnswerRequest body, so that DTO must stay in the net too.
        serviceInterfaces.Add(typeof(Gert.Service.Chat.ITurnQuestions));

        var dtos = new HashSet<Type>();
        foreach (var svc in serviceInterfaces)
        {
            foreach (var method in svc.GetMethods())
            {
                foreach (var param in method.GetParameters())
                {
                    if (IsRequestDto(param.ParameterType))
                    {
                        dtos.Add(param.ParameterType);
                    }
                }
            }
        }

        return dtos.OrderBy(t => t.FullName, StringComparer.Ordinal).ToList();
    }

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

        // Only types we own — and not the streaming/event/result surface.
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
