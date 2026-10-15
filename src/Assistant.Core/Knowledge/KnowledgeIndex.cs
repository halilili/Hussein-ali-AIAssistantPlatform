using System.Security.Cryptography;
using System.Text;
using Assistant.Core.Llm;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using static Qdrant.Client.Grpc.Conditions;

namespace Assistant.Core.Knowledge;

public sealed record KnowledgeHit(string Source, string Heading, string Text, float Score);

/// <summary>
/// Long-term memory for tenant knowledge in Qdrant.
/// Multi-tenancy: ONE collection, every point carries tenant_id (keyword payload index) and every
/// search is filtered by it. Scales to hundreds of small tenants; no cross-tenant leakage by construction.
/// </summary>
public sealed class KnowledgeIndex(
    QdrantClient qdrant,
    EmbeddingService embeddings,
    IOptions<KnowledgeOptions> knowledgeOptions,
    IOptions<LlmOptions> llmOptions)
{
    private readonly KnowledgeOptions _opt = knowledgeOptions.Value;

    public async Task EnsureCollectionAsync(CancellationToken ct = default)
    {
        if (await qdrant.CollectionExistsAsync(_opt.Collection, ct)) return;

        await qdrant.CreateCollectionAsync(_opt.Collection,
            new VectorParams { Size = (ulong)llmOptions.Value.EmbeddingDimensions, Distance = Distance.Cosine },
            cancellationToken: ct);
        await qdrant.CreatePayloadIndexAsync(_opt.Collection, "tenant_id", PayloadSchemaType.Keyword, cancellationToken: ct);
        await qdrant.CreatePayloadIndexAsync(_opt.Collection, "source", PayloadSchemaType.Keyword, cancellationToken: ct);
    }

    /// <summary>Idempotent re-ingest of one document: delete its old chunks, then upsert the new ones.</summary>
    public async Task<int> ReplaceDocumentAsync(string tenantId, string source, IReadOnlyList<Chunk> chunks, CancellationToken ct = default)
    {
        await qdrant.DeleteAsync(_opt.Collection, MatchKeyword("tenant_id", tenantId) & MatchKeyword("source", source), cancellationToken: ct);
        if (chunks.Count == 0) return 0;

        var vectors = await embeddings.EmbedBatchAsync(chunks.Select(c => c.Text).ToList(), ct);
        var points = chunks.Select((c, i) => new PointStruct
        {
            Id = DeterministicId(tenantId, source, c.Index),
            Vectors = vectors[i],
            Payload =
            {
                ["tenant_id"] = tenantId,
                ["source"] = source,
                ["heading"] = c.Heading,
                ["chunk_index"] = c.Index,
                ["text"] = c.Text,
                ["ingested_at"] = DateTimeOffset.UtcNow.ToString("O")
            }
        }).ToList();

        await qdrant.UpsertAsync(_opt.Collection, points, cancellationToken: ct);
        return points.Count;
    }

    public async Task<IReadOnlyList<KnowledgeHit>> SearchAsync(string tenantId, string query, CancellationToken ct = default)
    {
        var vector = await embeddings.EmbedAsync(query, ct);
        var hits = await qdrant.SearchAsync(
            _opt.Collection,
            vector,
            filter: MatchKeyword("tenant_id", tenantId),     // tenant isolation: never optional
            limit: (ulong)_opt.TopK,
            payloadSelector: true,
            scoreThreshold: _opt.MinScore,
            cancellationToken: ct);

        return hits.Select(h => new KnowledgeHit(
            h.Payload["source"].StringValue,
            h.Payload["heading"].StringValue,
            h.Payload["text"].StringValue,
            h.Score)).ToList();
    }

    /// <summary>Same tenant+source+index always maps to the same point id → re-ingest overwrites, never duplicates.</summary>
    private static Guid DeterministicId(string tenantId, string source, int index)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId}|{source}|{index}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}
