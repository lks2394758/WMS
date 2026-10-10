namespace Wms.Domain.Common;

/// <summary>Outcome of an operation without a value. Business failures are returned, not thrown.</summary>
public readonly struct Result
{
    private Result(DomainError? error) => Error = error;

    public DomainError? Error { get; }
    public bool IsSuccess => Error is null;
    public bool IsFailure => Error is not null;

    public static Result Success() => new(null);
    public static Result Failure(DomainError error) => new(error ?? throw new ArgumentNullException(nameof(error)));

    public static implicit operator Result(DomainError error) => Failure(error);
}

/// <summary>Outcome of an operation that produces <typeparamref name="T"/> on success.</summary>
public readonly struct Result<T>
{
    private readonly T? _value;

    private Result(T? value, DomainError? error)
    {
        _value = value;
        Error = error;
    }

    public DomainError? Error { get; }
    public bool IsSuccess => Error is null;
    public bool IsFailure => Error is not null;

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Cannot read the value of a failed result ({Error!.Code}).");

    public static Result<T> Success(T value) => new(value, null);
    public static Result<T> Failure(DomainError error) => new(default, error ?? throw new ArgumentNullException(nameof(error)));

    public static implicit operator Result<T>(T value) => Success(value);
    public static implicit operator Result<T>(DomainError error) => Failure(error);
}
