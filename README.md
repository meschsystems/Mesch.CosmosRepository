# Mesch.CosmosRepository

A lightweight repository pattern implementation for Azure Cosmos DB with built-in support for multi-tenancy, hybrid caching, and type-safe error handling.

## Features

- **Convention-based Property Access** - Aggregates are automatically mapped using `Id` and `TenantId` properties
- **Multi-tenant by Default** - Partition key isolation is enforced at the repository level
- **Hybrid Caching** - Integrated with `Microsoft.Extensions.Caching.Hybrid` for automatic L1/L2 caching
- **Type-safe Error Handling** - `Result<T>` pattern eliminates exception-based control flow
- **Optimistic Concurrency** - ETag-based conflict detection prevents lost updates
- **Domain Events Support** - Infrastructure for processing domain events after persistence
- **Comprehensive Logging** - Structured logging with Cosmos DB metadata (ActivityId, RequestCharge, StatusCode)

## Installation

```bash
dotnet add package Mesch.CosmosRepository
```

## Configuration

### Container Setup

The repository requires two Cosmos DB containers to be configured via `CosmosContainerFactory`:

```csharp
services.AddSingleton<CosmosContainerFactory>(sp =>
{
    var cosmosClient = sp.GetRequiredService<CosmosClient>();
    var database = cosmosClient.GetDatabase("your-database");

    return new CosmosContainerFactory(
        database.GetContainer("items"),    // Main aggregate storage
        database.GetContainer("events")    // Domain events storage
    );
});
```

### Service Registration

```csharp
services.AddSingleton<CosmosClient>(sp =>
{
    return new CosmosClient(
        connectionString,
        new CosmosClientOptions
        {
            SerializerOptions = new CosmosSerializationOptions
            {
                PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
            }
        }
    );
});

services.AddHybridCache();
services.AddSingleton<CosmosRepository<YourAggregate, Guid>>();
```

## Usage

### Aggregate Requirements

Aggregates must satisfy the following conventions:

1. Must be a reference type (`class`)
2. Must have an `Id` property of type `TId` (struct constraint)
3. Must have a `TenantId` property for partition key isolation
4. Should include a `documentType` property for container queries (automatically filtered)

```csharp
public class Order
{
    public Guid Id { get; set; }
    public string TenantId { get; set; }
    public string documentType => nameof(Order);

    public string CustomerName { get; set; }
    public decimal TotalAmount { get; set; }
}
```

### Repository Operations

All repository methods return `Result<T>` for explicit error handling:

```csharp
public class OrderService
{
    private readonly CosmosRepository<Order, Guid> _repository;

    public OrderService(CosmosRepository<Order, Guid> repository)
    {
        _repository = repository;
    }

    public async Task<Result<Order?>> GetOrderAsync(Guid orderId)
    {
        var result = await _repository.GetByIdAsync(orderId);

        if (result.IsError)
        {
            // Handle error with full Cosmos DB context
            var error = result.Error;
            _logger.LogError(
                "Failed to retrieve order {OrderId}: {Message} (ActivityId: {ActivityId}, RU: {RequestCharge})",
                orderId,
                error.Message,
                error.ActivityId,
                error.RequestCharge
            );
            return result;
        }

        return result.Value;
    }

    public async Task<Result<bool>> CreateOrderAsync(Order order)
    {
        var result = await _repository.AddAsync(order);

        if (result.IsError)
        {
            // Error types include: CosmosError, ValidationError, SerializationError, etc.
            return result.Error;
        }

        return true;
    }

    public async Task<Result<bool>> UpdateOrderAsync(Order order)
    {
        var result = await _repository.UpdateAsync(order);

        if (result.IsError)
        {
            // Optimistic concurrency conflicts are surfaced as RepositoryErrorType.ConcurrencyConflict
            if (result.Error.Type == RepositoryErrorType.ConcurrencyConflict)
            {
                // Implement retry logic or conflict resolution
            }

            return result.Error;
        }

        return true;
    }
}
```

### Query All Documents

```csharp
var result = await _repository.GetAllAsync();

if (result.IsSuccess)
{
    var orders = result.Value;
    foreach (var order in orders)
    {
        // Process orders
    }
}
```

### Domain Events

The repository supports domain event emission via delegate-based handlers. Aggregates that implement `IDomainEventEmitter` can emit events that are automatically processed after successful persistence.

#### Implementing Domain Events

```csharp
public class OrderCreatedEvent : IDomainEvent
{
    public string EventId { get; } = Guid.NewGuid().ToString();
    public DateTimeOffset OccurredAt { get; } = DateTimeOffset.UtcNow;
    public string EventType => nameof(OrderCreatedEvent);

    public string OrderId { get; init; }
    public decimal TotalAmount { get; init; }
}

public class Order : IDomainEventEmitter
{
    private readonly List<IDomainEvent> _uncommittedEvents = new();

    public Guid Id { get; set; }
    public string TenantId { get; set; }
    public string documentType => nameof(Order);
    public string CustomerName { get; set; }
    public decimal TotalAmount { get; set; }

    public void MarkAsCreated()
    {
        _uncommittedEvents.Add(new OrderCreatedEvent
        {
            OrderId = Id.ToString(),
            TotalAmount = TotalAmount
        });
    }

    public IReadOnlyCollection<IDomainEvent> GetUncommittedEvents() => _uncommittedEvents.AsReadOnly();
    public void ClearUncommittedEvents() => _uncommittedEvents.Clear();
}
```

#### Configuring Event Handlers

```csharp
var repository = serviceProvider.GetRequiredService<CosmosRepository<Order, Guid>>();

repository.SetEventHandler(async context =>
{
    // Context includes: AggregateId, AggregateType, TenantId, SequenceNumber, Event, Aggregate
    _logger.LogInformation(
        "Event {EventType} for {AggregateType} {AggregateId} (Sequence: {SequenceNumber})",
        context.Event.EventType,
        context.AggregateType,
        context.AggregateId,
        context.SequenceNumber
    );

    // Access the full aggregate that was persisted
    var order = context.Aggregate;

    // Persist to event store
    await _eventsContainer.CreateItemAsync(new
    {
        id = context.Event.EventId,
        aggregateId = context.AggregateId,
        aggregateType = context.AggregateType,
        tenantId = context.TenantId,
        sequenceNumber = context.SequenceNumber,
        eventType = context.Event.EventType,
        eventData = context.Event,
        aggregateSnapshot = context.Aggregate, // Full aggregate state at event time
        occurredAt = context.Event.OccurredAt
    }, new PartitionKey(context.TenantId));

    // Publish to message bus, trigger side effects, etc.
});
```

#### Event Processing Behavior

- Events are processed **after** the aggregate is successfully persisted
- Events are processed **in order** based on sequence number
- If event processing fails, the entire operation is rolled back
- Events are automatically cleared after successful processing
- Event handlers receive full context including tenant isolation

### Error Handling Patterns

#### Pattern Matching

```csharp
var result = await _repository.GetByIdAsync(orderId);

result.Match(
    onSuccess: order => ProcessOrder(order),
    onError: error => LogError(error)
);
```

#### Try Pattern

```csharp
if (result.TryGetValue(out var order))
{
    // Success path
}
else if (result.TryGetError(out var error))
{
    // Error path with full context
    Console.WriteLine($"Error: {error.Message} (Type: {error.Type})");
}
```

### Caching Behavior

- **GetByIdAsync** - Results are cached with 30-minute expiration
- **GetAllAsync** - Collection queries bypass cache
- **AddAsync/UpdateAsync** - Cache is automatically updated on successful writes
- **DeleteAsync** - Cache entries are invalidated

Cache keys follow the pattern: `{AggregateType}:{Id}`

## Error Types

The repository returns structured errors via `RepositoryError`:

| Type | Description |
|------|-------------|
| `NotFound` | Entity does not exist in the container |
| `ConcurrencyConflict` | ETag mismatch during update (optimistic concurrency violation) |
| `CosmosError` | General Cosmos DB error (includes StatusCode and SubStatusCode) |
| `SerializationError` | JSON deserialization failure |
| `ValidationError` | Business rule validation failure |
| `NetworkError` | Network connectivity issue |
| `ThrottlingError` | Rate limit exceeded (429 responses) |

Each error includes:
- `Message` - Human-readable error description
- `Exception` - Original exception (if applicable)
- `ActivityId` - Cosmos DB activity identifier for support requests
- `RequestCharge` - RU consumption for the failed operation
- `StatusCode` - HTTP status code
- `SubStatusCode` - Cosmos-specific error code

## Result&lt;T&gt; API

The `Result<T>` type provides a type-safe alternative to exception-based error handling:

### Properties

- `IsSuccess` - Returns true if the operation succeeded
- `IsError` - Returns true if the operation failed
- `Value` - Gets the success value (throws if accessed on error result)
- `Error` - Gets the error details (throws if accessed on success result)

### Methods

- `TryGetValue(out T? value)` - Safely attempts to extract the success value
- `TryGetError(out RepositoryError? error)` - Safely attempts to extract the error
- `Match<TResult>(Func<T, TResult> onSuccess, Func<RepositoryError, TResult> onError)` - Pattern matching for functional composition

### Implicit Conversions

Results support implicit conversion from both success values and errors:

```csharp
// Implicit conversion from value
Result<Order> result = order;

// Implicit conversion from error
Result<Order> result = new RepositoryError("Not found", RepositoryErrorType.NotFound);
```

## Performance Considerations

- Single-document reads leverage hybrid cache (L1 memory + L2 distributed)
- Updates use optimistic concurrency to prevent lost updates
- All operations include request charge logging for cost monitoring
- Query operations use `MaxItemCount` limits to prevent unbounded result sets

## Requirements

- .NET 9.0 or later
- Azure Cosmos DB account
- `Microsoft.Azure.Cosmos` 3.54.0+
- `Microsoft.Extensions.Caching.Hybrid` 9.10.0+

## License

MIT License - Copyright © 2025 Mesch Systems
