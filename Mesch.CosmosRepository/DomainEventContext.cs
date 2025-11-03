namespace Mesch.CosmosRepository;

/// <summary>
/// Context information passed to domain event handlers
/// </summary>
public sealed record DomainEventContext<TAggregate>(
    string AggregateId,
    string AggregateType,
    string TenantId,
    long SequenceNumber,
    IDomainEvent Event,
    TAggregate Aggregate
) where TAggregate : class;
