namespace Mesch.CosmosRepository;

/// <summary>
/// Represents the result of a repository operation that can either succeed with a value or fail with an error
/// </summary>
/// <typeparam name="T">The type of the success value</typeparam>
public sealed class Result<T>
{
    private readonly T? _value;
    private readonly RepositoryError? _error;
    private readonly bool _isSuccess;

    private Result(T value)
    {
        _value = value;
        _error = null;
        _isSuccess = true;
    }

    private Result(RepositoryError error)
    {
        _value = default;
        _error = error ?? throw new ArgumentNullException(nameof(error));
        _isSuccess = false;
    }

    /// <summary>
    /// Gets whether this result represents a success
    /// </summary>
    public bool IsSuccess => _isSuccess;

    /// <summary>
    /// Gets whether this result represents an error
    /// </summary>
    public bool IsError => !_isSuccess;

    /// <summary>
    /// Gets the success value. Throws if the result is an error.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when attempting to access value on an error result</exception>
    public T Value => _isSuccess
        ? _value!
        : throw new InvalidOperationException("Cannot access value of a failed result. Check IsSuccess before accessing Value.");

    /// <summary>
    /// Gets the error. Throws if the result is a success.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when attempting to access error on a success result</exception>
    public RepositoryError Error => !_isSuccess
        ? _error!
        : throw new InvalidOperationException("Cannot access error of a successful result. Check IsError before accessing Error.");

    /// <summary>
    /// Tries to get the success value
    /// </summary>
    /// <param name="value">The success value if the result is successful, otherwise default</param>
    /// <returns>True if the result is successful, otherwise false</returns>
    public bool TryGetValue(out T? value)
    {
        value = _isSuccess ? _value : default;
        return _isSuccess;
    }

    /// <summary>
    /// Tries to get the error
    /// </summary>
    /// <param name="error">The error if the result is an error, otherwise null</param>
    /// <returns>True if the result is an error, otherwise false</returns>
    public bool TryGetError(out RepositoryError? error)
    {
        error = !_isSuccess ? _error : null;
        return !_isSuccess;
    }

    /// <summary>
    /// Executes one of two functions depending on whether this is a success or error result
    /// </summary>
    public TResult Match<TResult>(Func<T, TResult> onSuccess, Func<RepositoryError, TResult> onError)
    {
        return _isSuccess ? onSuccess(_value!) : onError(_error!);
    }

    /// <summary>
    /// Executes one of two actions depending on whether this is a success or error result
    /// </summary>
    public void Match(Action<T> onSuccess, Action<RepositoryError> onError)
    {
        if (_isSuccess)
        {
            onSuccess(_value!);
        }
        else
        {
            onError(_error!);
        }
    }

    /// <summary>
    /// Creates a successful result
    /// </summary>
    public static Result<T> Success(T value) => new(value);

    /// <summary>
    /// Creates an error result
    /// </summary>
    public static Result<T> Failure(RepositoryError error) => new(error);

    /// <summary>
    /// Implicitly converts a value to a successful result
    /// </summary>
    public static implicit operator Result<T>(T value) => new(value);

    /// <summary>
    /// Implicitly converts a RepositoryError to an error result
    /// </summary>
    public static implicit operator Result<T>(RepositoryError error) => new(error);
}
