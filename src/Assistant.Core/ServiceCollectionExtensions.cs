using Assistant.Core.Conversations;
using Assistant.Core.Knowledge;
using Assistant.Core.Llm;
using Assistant.Core.Messaging;
using Assistant.Core.Tenants;
using Assistant.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            var cs = config.GetConnectionString("Redis") ?? throw new InvalidOperationException("ConnectionStrings:Redis is missing");
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategories.Redis + ".Connection");
            var mux = ConnectionMultiplexer.Connect(cs);
            // Connection events are what explains a RedisTimeoutException afterwards (incident Step 3: RPUSH timeout).
            mux.ConnectionFailed += (_, e) => log.LogWarning(e.Exception, "Redis connection FAILED {EndPoint} ({FailureType})", e.EndPoint, e.FailureType);
            mux.ConnectionRestored += (_, e) => log.LogInformation("Redis connection restored {EndPoint}", e.EndPoint);
            mux.ErrorMessage += (_, e) => log.LogWarning("Redis server error from {EndPoint}: {Message}", e.EndPoint, e.Message);
            log.LogInformation("Redis connected to {EndPoints} (IsConnected={Connected})",
                string.Join(",", mux.GetEndPoints().Select(ep => ep.ToString())), mux.IsConnected);
            return mux;
        });
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
            sp.GetRequiredService<OpenAIClient>().GetEmbeddingClient(sp.GetRequiredService<IOptions<LlmOptions>>().Value.EmbeddingModel),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategories.Llm + ".Embeddings")));

        services.AddSingleton(sp =>
        {
            var o = sp.GetRequiredService<IOptions<KnowledgeOptions>>().Value;
            return new QdrantClient(o.QdrantHost, o.QdrantGrpcPort);
        });
        services.AddSingleton<KnowledgeIndex>();
        return services;
    }
}
