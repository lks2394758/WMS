using Wms.Contracts.V2.Common;
using Wms.Domain.Common;

namespace Wms.Application.V2.Common;

/// <summary>A v2 use-case failure: stable code, message, optional field (400) and the objects it concerns.</summary>
public sealed record V2Failure(string Code, string Message, string? Field = null, IReadOnlyList<SubjectRef>? Subjects = null)
{
    public static V2Failure From(DomainError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new V2Failure(error.Code, error.Message, error.Field);
    }

    public static V2Failure Validation(string field, string message) => new(ProblemCodesV2.ValidationFailed, message, field);

    public static V2Failure NotFound(string what, SubjectRef? subject = null) =>
        new(ProblemCodesV2.NotFound, $"{what} was not found.", Subjects: subject is null ? null : [subject]);

    public static V2Failure VersionConflict(SubjectRef subject) =>
        new(ProblemCodesV2.VersionConflict, "The record was changed by someone else. Reload and try again.", Subjects: [subject]);
}

/// <summary>Outcome of a v2 use case; <see cref="Replayed"/> marks an idempotent replay of an earlier create.</summary>
public readonly struct V2Result<T>
{
    private readonly T? _value;

    private V2Result(T? value, V2Failure? failure, bool replayed)
    {
        _value = value;
        Failure = failure;
        Replayed = replayed;
    }

    public V2Failure? Failure { get; }

    public bool Replayed { get; }

    public bool IsSuccess => Failure is null;

    public T Value => IsSuccess ? _value! : throw new InvalidOperationException($"Failed result ({Failure!.Code}).");

    public static V2Result<T> Success(T value, bool replayed = false) => new(value, null, replayed);

    public static implicit operator V2Result<T>(T value) => new(value, null, false);

    public static implicit operator V2Result<T>(V2Failure failure) => new(default, failure ?? throw new ArgumentNullException(nameof(failure)), false);
}

/// <summary>Placeholder value for commands without a response body.</summary>
public sealed record V2None
{
    public static V2None Value { get; } = new();
}
