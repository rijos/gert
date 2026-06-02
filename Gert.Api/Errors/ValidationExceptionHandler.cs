using Gert.Service.Validation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Gert.Api.Errors;

/// <summary>
/// Maps a service-layer <see cref="ValidationException"/> (Change A, phase 1) to a
/// <b>400</b> Gert-branded <see cref="ProblemDetails"/> that lists the field errors
/// under the <c>errors</c> extension. Wired via <c>app.UseExceptionHandler</c> +
/// <c>AddExceptionHandler</c>; non-validation exceptions are left for the next
/// handler / the default 500 path. The brand marker + traceId are stamped by the
/// registered <c>AddProblemDetails</c> customizer (<see cref="GertProblem.Stamp"/>).
/// </summary>
public sealed class ValidationExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _problemDetails;

    public ValidationExceptionHandler(IProblemDetailsService problemDetails) =>
        _problemDetails = problemDetails ?? throw new ArgumentNullException(nameof(problemDetails));

    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (exception is not ValidationException validation)
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Bad Request",
            Detail = "One or more validation errors occurred.",
        };

        // Field errors → RFC-7807-style { property: [messages] } map.
        var errors = validation.Result.Errors
            .GroupBy(e => e.Property)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => e.Message).ToArray());
        problem.Extensions["errors"] = errors;

        return await _problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
        }).ConfigureAwait(false);
    }
}
