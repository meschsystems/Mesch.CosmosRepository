using Microsoft.Azure.Cosmos;

namespace Mesch.CosmosRepository;

/// <summary>
/// Factory for creating Cosmos containers
/// </summary>
public class CosmosContainerFactory
{
    private readonly CosmosClient _cosmosClient;
    private readonly string _databaseName;

    public CosmosContainerFactory(CosmosClient cosmosClient, string databaseName)
    {
        _cosmosClient = cosmosClient;
        _databaseName = databaseName;
    }

    /// <summary>
    /// Gets the items container for aggregate storage
    /// </summary>
    public Container GetItemsContainer() => _cosmosClient.GetContainer(_databaseName, "Items");

    /// <summary>
    /// Gets the events container for domain event journaling
    /// </summary>
    public Container GetEventsContainer() => _cosmosClient.GetContainer(_databaseName, "Journals");
}