using System.Diagnostics;
using System.Text.Json;
using Assistant.Core.Messaging;
using Assistant.Logging;
using Microsoft.Extensions.Logging;
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
public sealed class ConversationStore(IConnectionMultiplexer redis, ILoggerFactory loggers)
{
    private readonly ILogger _log = loggers.CreateLogger(LogCategories.Redis + ".Conversation");

    private const int MaxTurns = 50;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    public static string Key(string tenantId, string phone) => $"conv:{tenantId}:{phone}";

    public async Task AppendAsync(string tenantId, string phone, params ChatTurn[] turns)
    {
        var db = redis.GetDatabase();
        var key = Key(tenantId, phone);
        var values = turns.Select(t => (RedisValue)JsonSerializer.Serialize(t)).ToArray();
        // Three plain commands instead of MULTI/EXEC: a transaction hides the real Redis error behind
        // "EXECABORT Transaction discarded because of previous errors" (incident, Step 9). Atomicity is not needed
        // here: if LTRIM/EXPIRE were skipped the list is merely a little longer until the next append.
        var start = Stopwatch.GetTimestamp();
        long length = 0;
        await RedisRetry.WhileLoadingAsync(async () =>
        {
            length = await db.ListRightPushAsync(key, values);
            await db.ListTrimAsync(key, -MaxTurns, -1);
            await db.KeyExpireAsync(key, Retention);
        });
        _log.LogInformation("RPUSH {Key} +{Count} turn(s) [{Roles}] → length {Length}; LTRIM -{Max} -1; EXPIRE {Days}d ({Ms:0} ms)",
            key, turns.Length, string.Join(",", turns.Select(t => t.Role)), length, MaxTurns, Retention.TotalDays,
            Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }

    public async Task<IReadOnlyList<ChatTurn>> GetRecentAsync(string tenantId, string phone, int count)
    {
        var start = Stopwatch.GetTimestamp();
        var items = await RedisRetry.WhileLoadingAsync(() => redis.GetDatabase().ListRangeAsync(Key(tenantId, phone), -count, -1));
        _log.LogInformation("LRANGE {Key} -{Count} -1 → {Found} turn(s) ({Ms:0} ms)",
            Key(tenantId, phone), count, items.Length, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        return items.Select(i => JsonSerializer.Deserialize<ChatTurn>(i.ToString())!).ToList();
    }

    public async Task ClearAsync(string tenantId, string phone)
    {
        var deleted = await redis.GetDatabase().KeyDeleteAsync(Key(tenantId, phone));
        _log.LogInformation("DEL {Key} → {Deleted}", Key(tenantId, phone), deleted);
    }
}
