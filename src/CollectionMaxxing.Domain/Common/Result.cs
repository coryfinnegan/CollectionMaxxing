namespace CollectionMaxxing.Domain.Common;

/// <summary>What went wrong, in terms the API layer can turn into an HTTP status.</summary>
public enum ErrorKind
{
    /// <summary>The input is invalid on its own terms (400).</summary>
    Validation,

    /// <summary>The thing asked for doesn't exist (404).</summary>
    NotFound,

    /// <summary>The input is fine but clashes with current state (409).</summary>
    Conflict,

    /// <summary>The caller is a member but their role doesn't allow this particular request (403).</summary>
    Forbidden,
}

/// <summary>A business-rule failure. <c>Code</c> is stable and machine readable, e.g. <c>location.cycle</c>.</summary>
public sealed record DomainError(ErrorKind Kind, string Code, string Message, string? Field = null);

/// <summary>Outcome of an operation that returns nothing on success.</summary>
public class Result
{
    protected Result(DomainError? error) => Error = error;

    public DomainError? Error { get; }

    public bool IsSuccess => Error is null;

    public static Result Success() => new(null);

    public static Result Failure(DomainError error) => new(error);
}

/// <summary>Outcome of an operation that returns a value on success.</summary>
public sealed class Result<T> : Result
{
    private readonly T? _value;

    private Result(T? value, DomainError? error)
        : base(error) => _value = value;

    /// <summary>The value. Only valid when <see cref="Result.IsSuccess"/> is true.</summary>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"No value: the operation failed with {Error!.Code}.");

    public static Result<T> Success(T value) => new(value, null);

    public static new Result<T> Failure(DomainError error) => new(default, error);

    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(DomainError error) => Failure(error);
}
