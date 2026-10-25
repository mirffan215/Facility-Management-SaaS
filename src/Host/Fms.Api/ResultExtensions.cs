using Fms.BuildingBlocks.Results;

namespace Fms.Api;

public static class ResultExtensions
{
    /// <summary>Maps a use-case result to an HTTP response with RFC 7807 problem details for every failure.</summary>
    public static IResult ToHttp<T>(this Result<T> result, Func<T, IResult>? ok = null) => result.Status switch
    {
        ResultStatus.Ok => ok is null ? Results.Ok(result.Value) : ok(result.Value!),
        ResultStatus.NotFound => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: result.Message ?? "Not found"),
        ResultStatus.Invalid => Results.ValidationProblem(result.Errors!.ToDictionary(e => e.Key, e => e.Value), title: "Validation failed"),
        ResultStatus.Conflict => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: result.Message ?? "Conflict"),
        ResultStatus.Forbidden => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: result.Message ?? "Forbidden"),
        _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
    };
}
