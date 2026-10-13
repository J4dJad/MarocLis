namespace MarocLIS.Domain.Common;

/// <summary>
/// An error carrying a resource key rather than a literal message, so the same failure renders in
/// French, English or Arabic depending on the operator's UI culture.
/// <para>
/// <c>Args</c> elements are nullable, matching <c>ILocalizer.Format</c>, which is the only thing
/// that ever consumes them and which has always accepted <c>object?[]</c>. Many of the values
/// substituted into these messages are optional fields — a retention date, an expiry, an
/// accreditation number — and demanding non-null here only forced the call site to invent a
/// placeholder for something the message is meant to report as absent.
/// </para>
/// </summary>
public sealed record Error(string Code, string ResourceKey, params object?[] Args)
{
    public static readonly Error None = new(string.Empty, string.Empty);

    public static Error Validation(string key, params object?[] args) => new("validation", key, args);
    public static Error NotFound(string key, params object?[] args) => new("not_found", key, args);
    public static Error Conflict(string key, params object?[] args) => new("conflict", key, args);
    public static Error Forbidden(string key, params object?[] args) => new("forbidden", key, args);
    public static Error Regulatory(string key, params object?[] args) => new("regulatory", key, args);
}

public class Result
{
    protected Result(bool isSuccess, IReadOnlyList<Error> errors)
    {
        if (isSuccess && errors.Count > 0)
            throw new InvalidOperationException("A successful result cannot carry errors.");
        if (!isSuccess && errors.Count == 0)
            throw new InvalidOperationException("A failed result must carry at least one error.");

        IsSuccess = isSuccess;
        Errors = errors;
    }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public IReadOnlyList<Error> Errors { get; }
    public Error FirstError => Errors.Count > 0 ? Errors[0] : Error.None;

    public static Result Success() => new(true, []);
    public static Result Failure(Error error) => new(false, [error]);
    public static Result Failure(IReadOnlyList<Error> errors) => new(false, errors);

    public static Result<T> Success<T>(T value) => Result<T>.Ok(value);
    public static Result<T> Failure<T>(Error error) => Result<T>.Fail(error);
    public static Result<T> Failure<T>(IReadOnlyList<Error> errors) => Result<T>.Fail(errors);

    /// <summary>Collapses many results; keeps every error so a form can highlight all bad fields at once.</summary>
    public static Result Combine(params Result[] results)
    {
        var errors = results.Where(r => r.IsFailure).SelectMany(r => r.Errors).ToList();
        return errors.Count == 0 ? Success() : Failure(errors);
    }
}

public sealed class Result<T> : Result
{
    private readonly T? _value;

    private Result(T? value, bool isSuccess, IReadOnlyList<Error> errors) : base(isSuccess, errors)
        => _value = value;

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("Cannot read the value of a failed result.");

    internal static Result<T> Ok(T value) => new(value, true, []);
    internal static Result<T> Fail(Error error) => new(default, false, [error]);
    internal static Result<T> Fail(IReadOnlyList<Error> errors) => new(default, false, errors);

    public Result<TOut> Map<TOut>(Func<T, TOut> selector) =>
        IsSuccess ? Result<TOut>.Ok(selector(Value)) : Result<TOut>.Fail(Errors);
}
