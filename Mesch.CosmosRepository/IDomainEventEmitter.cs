namespace Mesch.CosmosRepository;

/// <summary>
/// Interface for aggregates that emit domain events
/// </summary>
public interface IDomainEventEmitter
{
    /// <summary>
    /// Gets the collection of uncommitted domain events
    /// </summary>
    IReadOnlyCollection<IDomainEvent> GetUncommittedEvents();

    /// <summary>
    /// Clears all uncommitted domain events (called after successful persistence)
    /// </summary>
    void ClearUncommittedEvents();
}
