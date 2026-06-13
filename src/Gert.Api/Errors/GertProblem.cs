using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Gert.Api.Errors;

/// <summary>
/// The single place that produces Gert-branded <see cref="ProblemDetails"/> - one
/// consistent <c>application/problem+json</c> contract for every non-success path
/// (401 / 403 / 404 / 400). Every problem carries the brand marker
/// <c>extensions["service"] = "gert"</c> and a <c>traceId</c>, so unauthorized /
/// over / missing / invalid traffic never returns an empty body or the SPA shell.
/// <para>
/// <see cref="Stamp"/> is the customizer wired into <c>AddProblemDetails</c> (it
/// runs for every framework-produced problem); <see cref="WriteAsync"/> is the
/// helper the JwtBearer challenge/forbidden events and the <c>/api/*</c> fallback
/// call to emit a branded body where the framework would otherwise write nothing.
/// </para>
/// </summary>
public static class GertProblem
{
    /// <summary>The brand marker stamped into <c>extensions["service"]</c> on every problem.</summary>
    public const string ServiceName = "gert";

    /// <summary>
    /// Stamp the Gert brand marker + a <c>traceId</c> onto a problem. Wired as the
    /// <c>AddProblemDetails</c> customizer so it runs for every framework-produced
    /// ProblemDetails, and reused by <see cref="WriteAsync"/> for the hand-written ones.
    /// </summary>
    public static void Stamp(ProblemDetails problem, HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(problem);

        problem.Extensions["service"] = ServiceName;
        problem.Extensions["traceId"] =
            Activity.Current?.Id ?? httpContext?.TraceIdentifier;
    }

    /// <summary>
    /// Write a Gert-branded <see cref="ProblemDetails"/> directly to the response
    /// (status code + <c>application/problem+json</c> body), used where the framework
    /// emits an empty body by default (JwtBearer 401/403, the <c>/api/*</c> 404).
    /// Routed through <see cref="IProblemDetailsService"/> so the registered
    /// customizer (<see cref="Stamp"/>) and content negotiation still apply.
    /// </summary>
    public static async Task WriteAsync(
        HttpContext httpContext,
        int statusCode,
        string title,
        string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        httpContext.Response.StatusCode = statusCode;

        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
        };

        var service = httpContext.RequestServices.GetService(typeof(IProblemDetailsService))
            as IProblemDetailsService;

        if (service is not null)
        {
            await service.WriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                ProblemDetails = problem,
            }).ConfigureAwait(false);
            return;
        }

        // Fallback (should not happen once AddProblemDetails is registered): write
        // the branded body by hand so the contract holds even without the service.
        Stamp(problem, httpContext);
        httpContext.Response.ContentType = "application/problem+json";
        await httpContext.Response.WriteAsJsonAsync(problem).ConfigureAwait(false);
    }
}
