using System.Text.Json;
using StackExchange.Redis;

namespace Agent.Worker.Booking;

/// <summary>What the patient is about to confirm. One pending proposal per conversation.</summary>
public sealed record PendingBooking(int DoctorId, string DoctorName, DateTimeOffset StartsAt, string PatientName, string? Reason);

/// <summary>
/// Two-phase booking on WhatsApp: propose (hold the slot 5 min) → patient says "yes" → confirm.
/// The hold is a UX guarantee ("nobody takes it while you answer"); the DB unique index is the
/// correctness guarantee. The pending proposal lives here, keyed by conversation — so confirm_booking
/// needs no arguments and the LLM cannot confirm a different slot than the one the patient saw.
/// </summary>
public sealed class BookingHoldService(IConnectionMultiplexer redis)
{
    public static readonly TimeSpan HoldTtl = TimeSpan.FromMinutes(5);

    // Release only if WE own the hold (never delete another patient's hold).
    private const string ReleaseIfOwner =
        "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";

    private static string HoldKey(string tenantId, int doctorId, DateTimeOffset startsAt) =>
        $"hold:{tenantId}:{doctorId}:{startsAt.UtcDateTime:yyyyMMddHHmm}";

    private static string PendingKey(string tenantId, string phone) => $"pending:{tenantId}:{phone}";

    public async Task<bool> TryHoldAsync(string tenantId, int doctorId, DateTimeOffset startsAt, string phone)
    {
        var db = redis.GetDatabase();
        var key = HoldKey(tenantId, doctorId, startsAt);
        if (await db.StringSetAsync(key, phone, HoldTtl, When.NotExists)) return true;
        return await db.StringGetAsync(key) == phone;          // re-proposing your own slot is fine
    }

    public async Task<HashSet<DateTimeOffset>> GetHeldByOthersAsync(
        string tenantId, int doctorId, IReadOnlyList<DateTimeOffset> starts, string phone)
    {
        if (starts.Count == 0) return [];
        var keys = starts.Select(s => (RedisKey)HoldKey(tenantId, doctorId, s)).ToArray();
        var owners = await redis.GetDatabase().StringGetAsync(keys);
        return starts.Where((_, i) => owners[i].HasValue && owners[i] != phone).ToHashSet();
    }

    public async Task SavePendingAsync(string tenantId, string phone, PendingBooking pending)
    {
        var previous = await GetPendingAsync(tenantId, phone);
        if (previous is not null && (previous.DoctorId != pending.DoctorId || previous.StartsAt != pending.StartsAt))
            await ReleaseAsync(tenantId, previous.DoctorId, previous.StartsAt, phone);   // patient changed their mind

        await redis.GetDatabase().StringSetAsync(PendingKey(tenantId, phone), JsonSerializer.Serialize(pending), HoldTtl);
    }

    public async Task<PendingBooking?> GetPendingAsync(string tenantId, string phone)
    {
        var json = await redis.GetDatabase().StringGetAsync(PendingKey(tenantId, phone));
        return json.HasValue ? JsonSerializer.Deserialize<PendingBooking>(json.ToString()) : null;
    }

    public async Task ClearPendingAsync(string tenantId, string phone, PendingBooking pending)
    {
        await redis.GetDatabase().KeyDeleteAsync(PendingKey(tenantId, phone));
        await ReleaseAsync(tenantId, pending.DoctorId, pending.StartsAt, phone);
    }

    private Task ReleaseAsync(string tenantId, int doctorId, DateTimeOffset startsAt, string phone) =>
        redis.GetDatabase().ScriptEvaluateAsync(ReleaseIfOwner,
            [(RedisKey)HoldKey(tenantId, doctorId, startsAt)], [(RedisValue)phone]);
}
