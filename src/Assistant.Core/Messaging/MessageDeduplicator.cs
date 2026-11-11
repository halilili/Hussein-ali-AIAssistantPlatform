using Assistant.Logging;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Assistant.Core.Messaging;

/// <summary>
/// WhatsApp retries webhooks (at-least-once delivery). Claiming the provider message id with
/// SET NX guarantees each patient message is processed once, even across gateway replicas.
/// </summary>
public sealed class MessageDeduplicator(IConnectionMultiplexer redis, ILoggerFactory loggers)
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(24);
    private readonly ILogger _log = loggers.CreateLogger(LogCategories.Redis + ".Dedup");

    /// <returns>true the first time an id is seen; false for duplicates.</returns>
    public async Task<bool> TryClaimAsync(string externalMessageId)
    {
        var key = $"msg:seen:{externalMessageId}";
        var claimed = await RedisRetry.WhileLoadingAsync(() =>
            redis.GetDatabase().StringSetAsync(key, 1, Window, When.NotExists));
        _log.LogInformation("SET {Key} NX EX {Hours}h → {Result}", key, Window.TotalHours, claimed ? "claimed (new message)" : "DUPLICATE (ignored)");
        return claimed;
    }
}
