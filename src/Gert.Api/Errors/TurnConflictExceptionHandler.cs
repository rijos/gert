using Gert.Service.Chat;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Gert.Api.Errors;

/// <summary>
/// Maps the service-layer <see cref="TurnInProgressException"/> to a <b>409</b>
/// Gert-branded <see cref="ProblemDetails"/> (chat-and-tools.md section detached
/// turns: turns are serialized per conversation). The SPA disables the composer
/// while streaming, so this surfaces only on a second tab or a raced
/// double-send - the client should resubscribe to the running turn instead.
/// </summary>
public sealed class TurnConflictExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _problemDetails;

    public TurnConflictExceptionHandler(IProblemDetailsService problemDetails) =>
        _problemDetails = problemDetails ?? throw new ArgumentNullException(nameof(problemDetails));

    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (exception is not TurnInProgressException conflict)
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;

        return await _problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Conflict",
                Detail = conflict.Message,
            },
        }).ConfigureAwait(false);
    }
}
