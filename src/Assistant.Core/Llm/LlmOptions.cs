namespace Assistant.Core.Llm;

/// <summary>
/// One OpenAI-compatible endpoint serves chat + embeddings. Switching Ollama -> vLLM -> OpenAI/Azure OpenAI
/// is a configuration change (Endpoint, ApiKey, model names), never a code change.
/// </summary>
public sealed class LlmOptions
{
    public const string Section = "Llm";

    public string Provider { get; set; } = "Ollama";                    // informational: Ollama | vLLM | OpenAI
    public string Endpoint { get; set; } = "http://localhost:11434/v1";
    public string ApiKey { get; set; } = "ollama";                      // Ollama ignores it; cloud: use user-secrets
    public string ChatModel { get; set; } = "qwen2.5:7b";
    public string SmallChatModel { get; set; } = "qwen2.5:1.5b";        // used by the router in Lab 14
    public string EmbeddingModel { get; set; } = "bge-m3";
    public int EmbeddingDimensions { get; set; } = 1024;
    public int TimeoutSeconds { get; set; } = 120;                      // CPU inference is slow
}

public sealed class KnowledgeOptions
{
    public const string Section = "Knowledge";

    public string QdrantHost { get; set; } = "localhost";
    public int QdrantGrpcPort { get; set; } = 6334;
    public string Collection { get; set; } = "kb_chunks";
    public int TopK { get; set; } = 4;
    public float MinScore { get; set; } = 0.45f;
}
