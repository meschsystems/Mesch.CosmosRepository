using System.Net;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

namespace Mesch.CosmosRepository;


/// <summary>
/// Cosmos repository implementation using convention-based property access
/// </summary>
/// <typeparam name="TAggregate">Aggregate root type</typeparam>
/// <typeparam name="TId">Aggregate ID type</typeparam>
public class CosmosRepository<TAggregate, TId>
    where TAggregate : class
    where TId : struct
{
    private readonly Container _itemsContainer;
    private readonly Container _eventsContainer;
    private readonly HybridCache _hybridCache;
    private readonly ILogger<CosmosRepository<TAggregate, TId>> _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly string _aggregateTypeName;
    private readonly TimeSpan _cacheExpiry;
    private Func<DomainEventContext<TAggregate>, Task>? _eventHandler;

    public CosmosRepository(
        CosmosContainerFactory containerFactory,
        HybridCache hybridCache,
        ILogger<CosmosRepository<TAggregate, TId>> logger,
        JsonSerializerOptions jsonOptions)
    {
        _itemsContainer = containerFactory.GetItemsContainer();
        _eventsContainer = containerFactory.GetEventsContainer();
        _hybridCache = hybridCache;
        _logger = logger;
        _jsonOptions = jsonOptions;
        _aggregateTypeName = typeof(TAggregate).Name;
        _cacheExpiry = TimeSpan.FromMinutes(30);
    }

    /// <summary>
    /// Sets the domain event handler delegate
    /// </summary>
    public void SetEventHandler(Func<DomainEventContext<TAggregate>, Task> handler)
    {
        _eventHandler = handler;
    }

    /// <summary>
    /// Extracts tenant ID from aggregate using convention (TenantId property)
    /// </summary>
    private string GetTenantId(TAggregate entity)
    {
        var tenantIdProperty = typeof(TAggregate).GetProperty("TenantId");
        if (tenantIdProperty == null)
        {
            throw new InvalidOperationException($"Aggregate {typeof(TAggregate).Name} must have a TenantId property");
        }

        var tenantId = tenantIdProperty.GetValue(entity);
        return tenantId?.ToString() ?? throw new InvalidOperationException("TenantId cannot be null");
    }

    /// <summary>
    /// Extracts aggregate ID from aggregate using convention (Id property)
    /// </summary>
    private string GetAggregateId(TAggregate entity)
    {
        var idProperty = typeof(TAggregate).GetProperty("Id");
        if (idProperty == null)
        {
            throw new InvalidOperationException($"Aggregate {typeof(TAggregate).Name} must have an Id property");
        }

        var id = idProperty.GetValue(entity);
        return id?.ToString() ?? throw new InvalidOperationException("Id cannot be null");
    }

    /// <summary>
    /// Gets all aggregates from the container (bypasses cache for collection queries)
    /// </summary>
    public async Task<Result<IEnumerable<TAggregate>>> GetAllAsync()
    {
        try
        {
            _logger.LogDebug("Retrieving all {AggregateType} aggregates", _aggregateTypeName);

            var query = _itemsContainer.GetItemQueryIterator<TAggregate>(
                new QueryDefinition("SELECT * FROM c WHERE c.documentType = @documentType")
                    .WithParameter("@documentType", _aggregateTypeName),
                requestOptions: new QueryRequestOptions
                {
                    MaxItemCount = 1000
                });

            var results = new List<TAggregate>();

            while (query.HasMoreResults)
            {
                var response = await query.ReadNextAsync();
                _logger.LogDebug("Query response returned {Count} documents", response.Count());

                foreach (var aggregate in response)
                {
                    if (aggregate != null)
                    {
                        results.Add(aggregate);
                    }
                    else
                    {
                        _logger.LogWarning("Found null aggregate in query results");
                    }
                }
            }

            _logger.LogDebug("Retrieved {Count} {AggregateType} aggregates", results.Count, _aggregateTypeName);
            return results;
        }
        catch (CosmosException cosmosEx)
        {
            _logger.LogError(cosmosEx,
                "Cosmos DB error retrieving all {AggregateType} aggregates. " +
                "StatusCode: {StatusCode}, SubStatusCode: {SubStatusCode}, " +
                "ActivityId: {ActivityId}, RequestCharge: {RequestCharge}, " +
                "Message: {Message}, Resource: {Resource}",
                _aggregateTypeName,
                cosmosEx.StatusCode,
                cosmosEx.SubStatusCode,
                cosmosEx.ActivityId,
                cosmosEx.RequestCharge,
                cosmosEx.Message,
                cosmosEx.ResponseBody);

            return new RepositoryError(
                $"Cosmos DB error retrieving all {_aggregateTypeName} aggregates",
                RepositoryErrorType.CosmosError,
                cosmosEx,
                cosmosEx.ActivityId,
                cosmosEx.RequestCharge,
                cosmosEx.StatusCode,
                cosmosEx.SubStatusCode);
        }
        catch (JsonException jsonEx)
        {
            _logger.LogError(jsonEx,
                "JSON deserialization error retrieving all {AggregateType} aggregates. " +
                "Path: {Path}, LineNumber: {LineNumber}, BytePositionInLine: {BytePositionInLine}",
                _aggregateTypeName,
                jsonEx.Path,
                jsonEx.LineNumber,
                jsonEx.BytePositionInLine);

            return new RepositoryError(
                $"JSON deserialization error retrieving all {_aggregateTypeName} aggregates at {jsonEx.Path}",
                RepositoryErrorType.SerializationError,
                jsonEx);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unexpected error retrieving all {AggregateType} aggregates. " +
                "ExceptionType: {ExceptionType}, Message: {Message}, StackTrace: {StackTrace}",
                _aggregateTypeName,
                ex.GetType().Name,
                ex.Message,
                ex.StackTrace);

            return new RepositoryError(
                $"Unexpected error retrieving all {_aggregateTypeName} aggregates: {ex.Message}",
                RepositoryErrorType.CosmosError,
                ex);
        }
    }

    /// <summary>
    /// Gets an aggregate by ID with hybrid caching
    /// </summary>
    public async Task<Result<TAggregate?>> GetByIdAsync(TId id)
    {
        var idString = id.ToString();
        var cacheKey = $"{_aggregateTypeName}:{idString}";

        try
        {
            _logger.LogDebug("Retrieving {AggregateType} with ID {Id}", _aggregateTypeName, idString);

            var result = await _hybridCache.GetOrCreateAsync(
                cacheKey,
                async (cancellationToken) =>
                {
                    _logger.LogDebug("Cache miss for {AggregateType} {Id}, querying Cosmos", _aggregateTypeName, idString);

                    var query = _itemsContainer.GetItemQueryIterator<TAggregate>(
                        new QueryDefinition("SELECT * FROM c WHERE c.id = @id AND c.documentType = @documentType")
                            .WithParameter("@id", idString)
                            .WithParameter("@documentType", _aggregateTypeName),
                        requestOptions: new QueryRequestOptions
                        {
                            MaxItemCount = 1
                        });

                    while (query.HasMoreResults)
                    {
                        var response = await query.ReadNextAsync(cancellationToken);
                        var aggregate = response.FirstOrDefault();

                        if (aggregate != null)
                        {
                            _logger.LogDebug("Found {AggregateType} {Id} in Cosmos", _aggregateTypeName, idString);
                            return aggregate;
                        }
                    }

                    _logger.LogDebug("{AggregateType} {Id} not found", _aggregateTypeName, idString);
                    return null;
                },
                new HybridCacheEntryOptions
                {
                    Expiration = _cacheExpiry
                });

            if (result != null)
            {
                _logger.LogDebug("Retrieved {AggregateType} {Id} successfully", _aggregateTypeName, idString);
            }

            return result;
        }
        catch (CosmosException cosmosEx)
        {
            _logger.LogError(cosmosEx,
                "Cosmos DB error retrieving {AggregateType} with ID {Id}. " +
                "StatusCode: {StatusCode}, SubStatusCode: {SubStatusCode}, " +
                "ActivityId: {ActivityId}, RequestCharge: {RequestCharge}",
                _aggregateTypeName,
                idString,
                cosmosEx.StatusCode,
                cosmosEx.SubStatusCode,
                cosmosEx.ActivityId,
                cosmosEx.RequestCharge);

            return new RepositoryError(
                $"Cosmos DB error retrieving {_aggregateTypeName} with ID {idString}",
                RepositoryErrorType.CosmosError,
                cosmosEx,
                cosmosEx.ActivityId,
                cosmosEx.RequestCharge,
                cosmosEx.StatusCode,
                cosmosEx.SubStatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve {AggregateType} with ID {Id}", _aggregateTypeName, idString);

            return new RepositoryError(
                $"Failed to retrieve {_aggregateTypeName} with ID {idString}: {ex.Message}",
                RepositoryErrorType.CosmosError,
                ex);
        }
    }

    /// <summary>
    /// Adds a new aggregate with event processing
    /// </summary>
    public async Task<Result<bool>> AddAsync(TAggregate entity)
    {
        var idString = GetAggregateId(entity);
        var tenantId = GetTenantId(entity);
        var cacheKey = $"{_aggregateTypeName}:{idString}";

        try
        {
            _logger.LogDebug("Adding {AggregateType} with ID {Id}", _aggregateTypeName, idString);

            var response = await _itemsContainer.CreateItemAsync(
                entity,
                new PartitionKey(tenantId));

            _logger.LogDebug("Successfully added {AggregateType} {Id} to Cosmos", _aggregateTypeName, idString);

            await _hybridCache.SetAsync(cacheKey, entity, new HybridCacheEntryOptions
            {
                Expiration = _cacheExpiry
            });

            await ProcessDomainEventsAsync(entity, tenantId, 0);

            _logger.LogDebug("Successfully cached and processed events for {AggregateType} {Id}", _aggregateTypeName, idString);

            return true;
        }
        catch (CosmosException cosmosEx)
        {
            _logger.LogError(cosmosEx,
                "Cosmos DB error adding {AggregateType} with ID {Id}. " +
                "StatusCode: {StatusCode}, SubStatusCode: {SubStatusCode}, " +
                "ActivityId: {ActivityId}, RequestCharge: {RequestCharge}",
                _aggregateTypeName,
                idString,
                cosmosEx.StatusCode,
                cosmosEx.SubStatusCode,
                cosmosEx.ActivityId,
                cosmosEx.RequestCharge);

            return new RepositoryError(
                $"Cosmos DB error adding {_aggregateTypeName} with ID {idString}",
                RepositoryErrorType.CosmosError,
                cosmosEx,
                cosmosEx.ActivityId,
                cosmosEx.RequestCharge,
                cosmosEx.StatusCode,
                cosmosEx.SubStatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add {AggregateType} with ID {Id}", _aggregateTypeName, idString);

            return new RepositoryError(
                $"Failed to add {_aggregateTypeName} with ID {idString}: {ex.Message}",
                RepositoryErrorType.CosmosError,
                ex);
        }
    }

    /// <summary>
    /// Updates an existing aggregate with optimistic concurrency and event processing
    /// </summary>
    public async Task<Result<bool>> UpdateAsync(TAggregate entity)
    {
        var idString = GetAggregateId(entity);
        var tenantId = GetTenantId(entity);
        var cacheKey = $"{_aggregateTypeName}:{idString}";

        try
        {
            _logger.LogDebug("Updating {AggregateType} with ID {Id}", _aggregateTypeName, idString);

            var currentResponse = await _itemsContainer.ReadItemAsync<TAggregate>(
                idString,
                new PartitionKey(tenantId));

            var response = await _itemsContainer.ReplaceItemAsync(
                entity,
                idString,
                new PartitionKey(tenantId),
                new ItemRequestOptions
                {
                    IfMatchEtag = currentResponse.ETag
                });

            _logger.LogDebug("Successfully updated {AggregateType} {Id} in Cosmos", _aggregateTypeName, idString);

            await _hybridCache.SetAsync(cacheKey, entity, new HybridCacheEntryOptions
            {
                Expiration = _cacheExpiry
            });

            var timestampProperty = currentResponse.Resource?.GetType().GetProperty("_ts");
            var timestamp = timestampProperty?.GetValue(currentResponse.Resource) as long? ?? 0;
            await ProcessDomainEventsAsync(entity, tenantId, timestamp);

            _logger.LogDebug("Successfully cached and processed events for {AggregateType} {Id}", _aggregateTypeName, idString);

            return true;
        }
        catch (CosmosException cosmosEx) when (cosmosEx.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            _logger.LogWarning(
                "Optimistic concurrency conflict updating {AggregateType} {Id}. " +
                "ActivityId: {ActivityId}, RequestCharge: {RequestCharge}",
                _aggregateTypeName,
                idString,
                cosmosEx.ActivityId,
                cosmosEx.RequestCharge);

            return new RepositoryError(
                $"Concurrency conflict updating {_aggregateTypeName} {idString}. The entity was modified by another process.",
                RepositoryErrorType.ConcurrencyConflict,
                cosmosEx,
                cosmosEx.ActivityId,
                cosmosEx.RequestCharge,
                cosmosEx.StatusCode,
                cosmosEx.SubStatusCode);
        }
        catch (CosmosException cosmosEx)
        {
            _logger.LogError(cosmosEx,
                "Cosmos DB error updating {AggregateType} with ID {Id}. " +
                "StatusCode: {StatusCode}, SubStatusCode: {SubStatusCode}, " +
                "ActivityId: {ActivityId}, RequestCharge: {RequestCharge}",
                _aggregateTypeName,
                idString,
                cosmosEx.StatusCode,
                cosmosEx.SubStatusCode,
                cosmosEx.ActivityId,
                cosmosEx.RequestCharge);

            return new RepositoryError(
                $"Cosmos DB error updating {_aggregateTypeName} with ID {idString}",
                RepositoryErrorType.CosmosError,
                cosmosEx,
                cosmosEx.ActivityId,
                cosmosEx.RequestCharge,
                cosmosEx.StatusCode,
                cosmosEx.SubStatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update {AggregateType} with ID {Id}", _aggregateTypeName, idString);

            return new RepositoryError(
                $"Failed to update {_aggregateTypeName} with ID {idString}: {ex.Message}",
                RepositoryErrorType.CosmosError,
                ex);
        }
    }

    /// <summary>
    /// Deletes an aggregate and invalidates cache
    /// </summary>
    public async Task<Result<bool>> DeleteAsync(TId id)
    {
        var idString = id.ToString();
        var cacheKey = $"{_aggregateTypeName}:{idString}";

        try
        {
            _logger.LogDebug("Deleting {AggregateType} with ID {Id}", _aggregateTypeName, idString);

            var existingResult = await GetByIdAsync(id);
            if (existingResult.IsError)
            {
                return existingResult.Error;
            }

            var existing = existingResult.Value;
            if (existing == null)
            {
                _logger.LogWarning("{AggregateType} {Id} not found for deletion", _aggregateTypeName, idString);
                return new RepositoryError(
                    $"{_aggregateTypeName} {idString} not found for deletion",
                    RepositoryErrorType.NotFound);
            }

            var tenantId = GetTenantId(existing);

            await _itemsContainer.DeleteItemAsync<TAggregate>(
                idString,
                new PartitionKey(tenantId));

            _logger.LogDebug("Successfully deleted {AggregateType} {Id} from Cosmos", _aggregateTypeName, idString);

            await _hybridCache.RemoveAsync(cacheKey);

            _logger.LogDebug("Successfully removed {AggregateType} {Id} from cache", _aggregateTypeName, idString);

            return true;
        }
        catch (CosmosException cosmosEx) when (cosmosEx.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning(
                "{AggregateType} {Id} not found for deletion. " +
                "ActivityId: {ActivityId}, RequestCharge: {RequestCharge}",
                _aggregateTypeName,
                idString,
                cosmosEx.ActivityId,
                cosmosEx.RequestCharge);

            return new RepositoryError(
                $"{_aggregateTypeName} {idString} not found for deletion",
                RepositoryErrorType.NotFound,
                cosmosEx,
                cosmosEx.ActivityId,
                cosmosEx.RequestCharge,
                cosmosEx.StatusCode,
                cosmosEx.SubStatusCode);
        }
        catch (CosmosException cosmosEx)
        {
            _logger.LogError(cosmosEx,
                "Cosmos DB error deleting {AggregateType} with ID {Id}. " +
                "StatusCode: {StatusCode}, SubStatusCode: {SubStatusCode}, " +
                "ActivityId: {ActivityId}, RequestCharge: {RequestCharge}",
                _aggregateTypeName,
                idString,
                cosmosEx.StatusCode,
                cosmosEx.SubStatusCode,
                cosmosEx.ActivityId,
                cosmosEx.RequestCharge);

            return new RepositoryError(
                $"Cosmos DB error deleting {_aggregateTypeName} with ID {idString}",
                RepositoryErrorType.CosmosError,
                cosmosEx,
                cosmosEx.ActivityId,
                cosmosEx.RequestCharge,
                cosmosEx.StatusCode,
                cosmosEx.SubStatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete {AggregateType} with ID {Id}", _aggregateTypeName, idString);

            return new RepositoryError(
                $"Failed to delete {_aggregateTypeName} with ID {idString}: {ex.Message}",
                RepositoryErrorType.CosmosError,
                ex);
        }
    }

    /// <summary>
    /// Processes domain events from the aggregate
    /// </summary>
    private async Task ProcessDomainEventsAsync(TAggregate entity, string tenantId, long lastSequenceNumber)
    {
        if (_eventHandler == null)
        {
            return;
        }

        if (entity is not IDomainEventEmitter emitter)
        {
            return;
        }

        var events = emitter.GetUncommittedEvents();
        if (events.Count == 0)
        {
            return;
        }

        var aggregateId = GetAggregateId(entity);
        var sequenceNumber = lastSequenceNumber;

        _logger.LogDebug(
            "Processing {EventCount} domain events for {AggregateType} {Id}",
            events.Count,
            _aggregateTypeName,
            aggregateId);

        foreach (var domainEvent in events)
        {
            sequenceNumber++;

            var context = new DomainEventContext<TAggregate>(
                AggregateId: aggregateId,
                AggregateType: _aggregateTypeName,
                TenantId: tenantId,
                SequenceNumber: sequenceNumber,
                Event: domainEvent,
                Aggregate: entity
            );

            try
            {
                await _eventHandler(context);

                _logger.LogDebug(
                    "Successfully processed event {EventType} (Sequence: {SequenceNumber}) for {AggregateType} {Id}",
                    domainEvent.EventType,
                    sequenceNumber,
                    _aggregateTypeName,
                    aggregateId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to process event {EventType} (Sequence: {SequenceNumber}) for {AggregateType} {Id}",
                    domainEvent.EventType,
                    sequenceNumber,
                    _aggregateTypeName,
                    aggregateId);

                // Re-throw to fail the entire operation
                throw;
            }
        }

        emitter.ClearUncommittedEvents();

        _logger.LogDebug(
            "Cleared {EventCount} uncommitted events for {AggregateType} {Id}",
            events.Count,
            _aggregateTypeName,
            aggregateId);
    }
}