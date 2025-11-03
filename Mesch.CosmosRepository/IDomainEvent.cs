namespace Mesch.CosmosRepository;

/// <summary>
/// Marker interface for domain events
/// </summary>
public interface IDomainEvent
{
    /// <summary>
    /// Timestamp when the event occurred
    /// </summary>
    DateTimeOffset OccurredAt { get; }

    /// <summary>
    /// Unique identifier for this event
    /// </summary>
    string EventId { get; }

    /// <summary>
    /// Type name of the event for serialization/routing
    /// </summary>
    string EventType { get; }
}
