using System.Text.Json;
using Assistant.Logging;
using StackExchange.Redis;

namespace Agent.Worker.Booking;

/// <summary>What the patient is about to confirm. One pending proposal per conversation.</summary>
public sealed record PendingBooking(int DoctorId, string DoctorName, DateTimeOffset StartsAt, string PatientName, string? Reason);

/// <summary>
/// Two-phase booking on WhatsApp: propose (hold the slot 5 min) → patient says "yes" → confirm.
/// The hold is a UX guarantee ("nobody takes it while you answer"); the DB unique index is the
/// correctness guarantee. The pending proposal lives here, keyed by conversation — so confirm_booking
/// needs no arguments and the LLM cannot confirm a different slot than the one the patient saw.
/// Every Redis command is logged to logs/redis-*.log (category Lab.Redis.Booking).
/// </summary>
public sealed class BookingHoldService(IConnectionMultiplexer redis, ILoggerFactory loggers)
{
    public static readonly TimeSpan HoldTtl = TimeSpan.FromMinutes(5);
    private readonly ILogger _log = loggers.CreateLogger(LogCategories.Redis + ".Booking");

    // Release only if WE own the hold (never delete another patient's hold).
    private const string ReleaseIfOwner =
        "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";

    // Public so diagnostics (/diag/state, /diag/flow) inspect exactly the keys the agent uses.
    public static string HoldKey(string tenantId, int doctorId, DateTimeOffset startsAt) =>
        $"hold:{tenantId}:{doctorId}:{startsAt.UtcDateTime:yyyyMMddHHmm}";

    public static string PendingKey(string tenantId, string phone) => $"pending:{tenantId}:{phone}";

    public async Task<bool> TryHoldAsync(string tenantId, int doctorId, DateTimeOffset startsAt, string phone)
    {
        var db = redis.GetDatabase();
        var key = HoldKey(tenantId, doctorId, startsAt);
        if (await db.StringSetAsync(key, phone, HoldTtl, When.NotExists))
        {
            _log.LogInformation("SET {Key} {Phone} NX EX {Ttl}s → acquired", key, phone, HoldTtl.TotalSeconds);
            return true;
        }
        var owner = await db.StringGetAsync(key);
        var mine = owner == phone;                              // re-proposing your own slot is fine
        _log.LogInformation("SET {Key} NX → exists, owner {Owner} → {Result}", key, owner.ToString(), mine ? "already yours" : "HELD BY ANOTHER PATIENT");
        return mine;
    }

    public async Task<HashSet<DateTimeOffset>> GetHeldByOthersAsync(
        string tenantId, int doctorId, IReadOnlyList<DateTimeOffset> starts, string phone)
    {
        if (starts.Count == 0) return [];
        var keys = starts.Select(s => (RedisKey)HoldKey(tenantId, doctorId, s)).ToArray();
        var owners = await redis.GetDatabase().StringGetAsync(keys);
        var held = starts.Where((_, i) => owners[i].HasValue && owners[i] != phone).ToHashSet();
        _log.LogInformation("MGET {Count} hold key(s) hold:{Tenant}:{Doctor}:* → {Held} held by other patients",
            keys.Length, tenantId, doctorId, held.Count);
        return held;
    }

    public async Task SavePendingAsync(string tenantId, string phone, PendingBooking pending)
    {
        var previous = await GetPendingAsync(tenantId, phone);
        if (previous is not null && (previous.DoctorId != pending.DoctorId || previous.StartsAt != pending.StartsAt))
            await ReleaseAsync(tenantId, previous.DoctorId, previous.StartsAt, phone);   // patient changed their mind

        var key = PendingKey(tenantId, phone);
        var json = JsonSerializer.Serialize(pending);
        await redis.GetDatabase().StringSetAsync(key, json, HoldTtl);
        _log.LogInformation("SET {Key} EX {Ttl}s {Json}", key, HoldTtl.TotalSeconds, json);
    }

    public async Task<PendingBooking?> GetPendingAsync(string tenantId, string phone)
    {
        var key = PendingKey(tenantId, phone);
        var json = await redis.GetDatabase().StringGetAsync(key);
        _log.LogInformation("GET {Key} → {Result}", key, json.HasValue ? json.ToString() : "(nil) — no pending proposal or expired");
        return json.HasValue ? JsonSerializer.Deserialize<PendingBooking>(json.ToString()) : null;
    }

    public async Task ClearPendingAsync(string tenantId, string phone, PendingBooking pending)
    {
        var key = PendingKey(tenantId, phone);
        var deleted = await redis.GetDatabase().KeyDeleteAsync(key);
        _log.LogInformation("DEL {Key} → {Deleted}", key, deleted);
        await ReleaseAsync(tenantId, pending.DoctorId, pending.StartsAt, phone);
    }

    private async Task ReleaseAsync(string tenantId, int doctorId, DateTimeOffset startsAt, string phone)
    {
        var key = HoldKey(tenantId, doctorId, startsAt);
        var result = await redis.GetDatabase().ScriptEvaluateAsync(ReleaseIfOwner, [(RedisKey)key], [(RedisValue)phone]);
        _log.LogInformation("EVAL release-if-owner {Key} {Phone} → {Result}", key, phone, (int)result == 1 ? "released" : "not ours / already expired");
    }
}
