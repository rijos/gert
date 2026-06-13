using FluentValidation;
using Gert.Model.Dtos;
using Gert.Service.Tools;

namespace Gert.Service.Validation.Validators;

/// <summary>
/// Validates a <see cref="ToolToggles"/> preference map (testing.md section 5: a toggle
/// key must name a <b>registered</b> tool). Entitlement (the JWT ceiling) is
/// authorization and is enforced elsewhere; this only rejects a toggle for a tool
/// the system does not have, so an unknown id can never reach the orchestrator.
/// </summary>
public sealed class ToolTogglesValidator : AbstractValidator<ToolToggles>
{
    public ToolTogglesValidator(ToolRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        RuleFor(t => t.Toggles)
            .Must(toggles => toggles.Keys.All(registry.Contains))
            .WithMessage(t =>
                "Unknown tool id(s): " +
                string.Join(", ", t.Toggles.Keys.Where(k => !registry.Contains(k))))
            .WithErrorCode("tools.unknown_id");
    }
}
