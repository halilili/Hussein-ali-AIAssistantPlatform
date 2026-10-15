using OpenAI.Embeddings;

namespace Assistant.Core.Llm;

/// <summary>
/// Thin wrapper over the OpenAI SDK embedding client (works against Ollama's /v1/embeddings).
/// Owning this interface insulates the platform from SDK churn and is where Lab 8 adds the embedding cache.
/// </summary>
public sealed class EmbeddingService(EmbeddingClient client)
{
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var result = await client.GenerateEmbeddingAsync(text, cancellationToken: ct);
        return result.Value.ToFloats().ToArray();
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (texts.Count == 0) return [];
        var result = await client.GenerateEmbeddingsAsync(texts, cancellationToken: ct);
        return result.Value.OrderBy(e => e.Index).Select(e => e.ToFloats().ToArray()).ToList();
    }
}
