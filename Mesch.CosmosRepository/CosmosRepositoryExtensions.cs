using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;

namespace Mesch.CosmosRepository;

/// <summary>
/// DI registration extensions for Cosmos repositories
/// </summary>
public static class CosmosRepositoryExtensions
{
    /// <summary>
    /// Registers Cosmos repository infrastructure (CosmosClient, HybridCache, etc.)
    /// Applications should register their specific repositories separately.
    /// </summary>
    public static IServiceCollection AddCosmosRepositoryInfrastructure(
        this IServiceCollection services,
        string connectionString,
        string databaseName)
    {
        // Register JsonSerializerOptions
        services.AddSingleton<JsonSerializerOptions>(new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        });

        // Register Cosmos client
        services.AddSingleton<CosmosClient>(provider =>
        {
            return new CosmosClient(connectionString, new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                SerializerOptions = new CosmosSerializationOptions
                {
                    PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
                }
            });
        });

        // Register container factory
        services.AddSingleton<CosmosContainerFactory>(provider =>
        {
            var cosmosClient = provider.GetRequiredService<CosmosClient>();
            return new CosmosContainerFactory(cosmosClient, databaseName);
        });

        // Register HybridCache
        services.AddHybridCache();

        return services;
    }
}