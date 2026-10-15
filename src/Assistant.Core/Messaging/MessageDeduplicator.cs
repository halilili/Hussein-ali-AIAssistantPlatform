using StackExchange.Redis;

namespace Assistant.Core.Messaging;

/// <summary>
/// WhatsApp retries webhooks (at-least-once delivery). Claiming the provider message id with
/// SET NX guarantees each patient message is processed once, even across gateway replicas.
/// </summary>
public sealed class MessageDeduplicator(IConnectionMultiplexer redis)
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(24);

    /// <returns>true the first time an id is seen; false for duplicates.</returns>
    public Task<bool> TryClaimAsync(string externalMessageId) =>
        redis.GetDatabase().StringSetAsync($"msg:seen:{externalMessageId}", 1, Window, When.NotExists);
}
