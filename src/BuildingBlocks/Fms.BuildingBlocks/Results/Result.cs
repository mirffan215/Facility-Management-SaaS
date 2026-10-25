namespace Fms.BuildingBlocks.Results;

public enum ResultStatus { Ok, NotFound, Invalid, Conflict, Forbidden }

/// <summary>Outcome of a use case, so services report expected failures without exceptions and the API maps them uniformly.</summary>
public sealed record Result<T>(ResultStatus Status, T? Value = default, string? Message = null, IReadOnlyDictionary<string, string[]>? Errors = null)
{
    public bool IsOk => Status == ResultStatus.Ok;

    public static Result<T> Ok(T value) => new(ResultStatus.Ok, value);
    public static Result<T> NotFound(string? message = null) => new(ResultStatus.NotFound, Message: message);
    public static Result<T> Conflict(string message) => new(ResultStatus.Conflict, Message: message);
    public static Result<T> Forbidden(string message) => new(ResultStatus.Forbidden, Message: message);
    public static Result<T> Invalid(string field, string error) =>
        new(ResultStatus.Invalid, Errors: new Dictionary<string, string[]> { [field] = [error] });
    public static Result<T> Invalid(IReadOnlyDictionary<string, string[]> errors) => new(ResultStatus.Invalid, Errors: errors);

    public Result<TOut> Map<TOut>(Func<T, TOut> map) => new(Status, IsOk ? map(Value!) : default, Message, Errors);
}

/// <summary>Collects field errors while validating a request.</summary>
public sealed class ValidationErrors
{
    private readonly Dictionary<string, List<string>> _errors = [];

    public bool Any => _errors.Count > 0;

    public void Add(string field, string error)
    {
        if (!_errors.TryGetValue(field, out var list)) _errors[field] = list = [];
        list.Add(error);
    }

    public Result<T> ToResult<T>() => Result<T>.Invalid(_errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));
}

/// <summary>A page of results.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, int Total, int PageNumber, int PageSize)
{
    public const int MaxPageSize = 100;

    public static (int Number, int Size) Normalize(int? page, int? pageSize) =>
        (Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? 25, 1, MaxPageSize));
}
