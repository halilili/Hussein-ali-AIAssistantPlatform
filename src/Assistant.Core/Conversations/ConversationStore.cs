using System.Text.Json;
using StackExchange.Redis;

namespace Assistant.Core.Conversations;

public sealed record ChatTurn(string Role, string Text, DateTimeOffset At)
{
    public const string User = "user";
    public const string Assistant = "assistant";
}

/// <summary>
/// Short-term memory: the last N turns per (tenant, phone) in a Redis list.
/// Only user text and the final assistant text are stored — never tool calls/results —
/// so history stays small and tool data is always re-fetched fresh (Lab 6 adds summarisation).
/// </summary>
public sealed class ConversationStore(IConnectionMultiplexer redis)
{
    private const int MaxTurns = 50;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    public static string Key(string tenantId, string phone) => $"conv:{tenantId}:{phone}";

    public async Task AppendAsync(string tenantId, string phone, params ChatTurn[] turns)
    {
        var db = redis.GetDatabase();
        var key = Key(tenantId, phone);
        var values = turns.Select(t => (RedisValue)JsonSerializer.Serialize(t)).ToArray();
        var tx = db.CreateTransaction();
        _ = tx.ListRightPushAsync(key, values);
        _ = tx.ListTrimAsync(key, -MaxTurns, -1);
        _ = tx.KeyExpireAsync(key, Retention);
        await tx.ExecuteAsync();
    }

    public async Task<IReadOnlyList<ChatTurn>> GetRecentAsync(string tenantId, string phone, int count)
    {
        var items = await redis.GetDatabase().ListRangeAsync(Key(tenantId, phone), -count, -1);
        return items.Select(i => JsonSerializer.Deserialize<ChatTurn>(i.ToString())!).ToList();
    }

    public Task ClearAsync(string tenantId, string phone) =>
        redis.GetDatabase().KeyDeleteAsync(Key(tenantId, phone));
}
