using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Fms.Api;

/// <summary>
/// A request the server cannot read (broken JSON, a value of the wrong type or an unknown enum name, a body too large) is the caller's mistake, not a server fault.
/// Without this the framework's bad-request exceptions reach the generic handler and come back as a 500.
/// </summary>
public sealed class BadRequestExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        var status = exception switch
        {
            BadHttpRequestException bad => bad.StatusCode,
            JsonException => StatusCodes.Status400BadRequest,
            _ => 0,
        };
        if (status == 0) return false;

        http.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = status == StatusCodes.Status413PayloadTooLarge ? "The request is too large." : "The request could not be read. Check the values you sent.",
            },
        });
    }
}
