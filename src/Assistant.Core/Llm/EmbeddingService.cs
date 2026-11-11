using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenAI.Embeddings;

namespace Assistant.Core.Llm;

/// <summary>
/// Thin wrapper over the OpenAI SDK embedding client (works against Ollama's /v1/embeddings).
/// Owning this interface insulates the platform from SDK churn and is where Lab 8 adds the embedding cache.
/// </summary>
public sealed class EmbeddingService(EmbeddingClient client, ILogger? log = null)
{
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var start = Stopwatch.GetTimestamp();
        var result = await client.GenerateEmbeddingAsync(text, cancellationToken: ct);
        var vector = result.Value.ToFloats().ToArray();
        log?.LogInformation("EMBED 1 text ({Chars} chars) → {Dims} dims ({Ms:0} ms)", text.Length, vector.Length,
            Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        return vector;
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (texts.Count == 0) return [];
        var start = Stopwatch.GetTimestamp();
        var result = await client.GenerateEmbeddingsAsync(texts, cancellationToken: ct);
        var vectors = result.Value.OrderBy(e => e.Index).Select(e => e.ToFloats().ToArray()).ToList();
        log?.LogInformation("EMBED batch of {Count} texts → {Dims} dims ({Ms:0} ms)", texts.Count, vectors.FirstOrDefault()?.Length ?? 0,
            Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        return vectors;
    }
}
