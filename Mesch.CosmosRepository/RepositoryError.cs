using System.Net;

namespace Mesch.CosmosRepository;

/// <summary>
/// Repository-specific error type with Cosmos DB metadata
/// </summary>
public sealed record RepositoryError(
    string Message,
    RepositoryErrorType Type,
    Exception? Exception = null,
    string? ActivityId = null,
    double? RequestCharge = null,
    HttpStatusCode? StatusCode = null,
    int? SubStatusCode = null
);

/// <summary>
/// Types of repository errors
/// </summary>
public enum RepositoryErrorType
{
    NotFound,
    ConcurrencyConflict,
    CosmosError,
    SerializationError,
    ValidationError,
    NetworkError,
    ThrottlingError
}
