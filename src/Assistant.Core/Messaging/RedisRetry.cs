using StackExchange.Redis;

namespace Assistant.Core.Messaging;

/// <summary>
/// Startup race: after Docker (re)starts, Redis rejects commands with "LOADING Redis is loading the dataset in memory"
/// for a few seconds, while RabbitMQ immediately delivers messages that were waiting. Instead of failing the patient's
/// message, wait briefly and retry. Only LOADING is retried — real errors (WRONGTYPE, OOM, MISCONF) surface at once.
/// </summary>
public static class RedisRetry
{
    private const int MaxAttempts = 15;                      // ~15 s worst case
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(1);

    public static async Task<T> WhileLoadingAsync<T>(Func<Task<T>> action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return await action(); }
            catch (RedisServerException ex) when (IsLoading(ex) && attempt < MaxAttempts)
            {
                await Task.Delay(Delay);
            }
        }
    }

    public static Task WhileLoadingAsync(Func<Task> action) =>
        WhileLoadingAsync(async () => { await action(); return true; });

    private static bool IsLoading(RedisServerException ex) =>
        ex.Message.StartsWith("LOADING", StringComparison.OrdinalIgnoreCase);
}
