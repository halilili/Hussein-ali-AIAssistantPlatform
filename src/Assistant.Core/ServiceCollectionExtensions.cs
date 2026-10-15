using Assistant.Core.Conversations;
using Assistant.Core.Knowledge;
using Assistant.Core.Llm;
using Assistant.Core.Messaging;
using Assistant.Core.Tenants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenAI;
using Qdrant.Client;
using StackExchange.Redis;

namespace Assistant.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>Postgres, Redis, tenants, conversations, de-duplication — needed by every host.</summary>
    public static IServiceCollection AddAssistantCore(this IServiceCollection services, IConfiguration config)
    {
        services.AddMemoryCache();
        services.AddSingleton(_ => NpgsqlDataSource.Create(config.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing")));
        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(config.GetConnectionString("Redis")
            ?? throw new InvalidOperationException("ConnectionStrings:Redis is missing")));
        services.AddSingleton<TenantStore>();
        services.AddSingleton<ConversationStore>();
        services.AddSingleton<MessageDeduplicator>();
        return services;
    }

    /// <summary>OpenAI-compatible client (Ollama/vLLM/OpenAI), embeddings and the Qdrant knowledge index.</summary>
    public static IServiceCollection AddAssistantKnowledge(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<LlmOptions>(config.GetSection(LlmOptions.Section));
        services.Configure<KnowledgeOptions>(config.GetSection(KnowledgeOptions.Section));

        services.AddSingleton(sp => LlmClientFactory.Create(sp.GetRequiredService<IOptions<LlmOptions>>().Value));
        services.AddSingleton(sp => new EmbeddingService(
            sp.GetRequiredService<OpenAIClient>().GetEmbeddingClient(sp.GetRequiredService<IOptions<LlmOptions>>().Value.EmbeddingModel)));

        services.AddSingleton(sp =>
        {
            var o = sp.GetRequiredService<IOptions<KnowledgeOptions>>().Value;
            return new QdrantClient(o.QdrantHost, o.QdrantGrpcPort);
        });
        services.AddSingleton<KnowledgeIndex>();
        return services;
    }
}
