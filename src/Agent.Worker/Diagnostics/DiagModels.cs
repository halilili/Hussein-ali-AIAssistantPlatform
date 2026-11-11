using System.Diagnostics;
using Agent.Worker.Booking;
using Agent.Worker.Clients;
using Assistant.Core.Tenants;
using Dapper;
using Npgsql;

namespace Agent.Worker.Diagnostics;

/// <summary>One check: what was tested, whether it passed, how long it took, what to look at if it failed.</summary>
public sealed record CheckResult(string Name, bool Ok, double Ms, object? Details = null, string? Error = null, string? Hint = null);

public sealed record DiagReport(string Area, bool Ok, double TotalMs, IReadOnlyList<CheckResult> Checks);

/// <summary>Row read by diagnostics only (the agent itself never reads the clinic DB — it uses Clinic.Api).</summary>
public sealed class DiagAppointmentRow
{
    public Guid Id { get; set; }
    public int DoctorId { get; set; }
    public string Doctor { get; set; } = "";
    public string Phone { get; set; } = "";
    public string? PatientName { get; set; }
    public DateTime StartsAtUtc { get; set; }
    public string Status { get; set; } = "";
    public string? Reason { get; set; }
    public string Source { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
}

internal static class Diag
{
    public static double Ms(long start) => Math.Round(Stopwatch.GetElapsedTime(start).TotalMilliseconds, 1);

    public static async Task<CheckResult> Check(string name, Func<Task<object?>> body, string? hint = null)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            var details = await body();                          // await FIRST: arguments are evaluated left to right,
            return new CheckResult(name, true, Ms(start), details);   // Ms(start) before the await always measured 0
        }
        catch (Exception ex)
        {
            return new CheckResult(name, false, Ms(start), null, $"{ex.GetType().Name}: {ex.Message}", hint);
        }
    }

    public static DiagReport Report(string area, long start, IReadOnlyList<CheckResult> checks) =>
        new(area, checks.All(c => c.Ok), Ms(start), checks);

    public static string Summary(DiagReport r) =>
        string.Join("; ", r.Checks.Select(c => c.Ok ? $"{c.Name}=OK" : $"{c.Name}=FAIL({c.Error})"));

    public static Dictionary<string, object?> ToDict(object row) =>
        ((IDictionary<string, object>)row).ToDictionary(kv => kv.Key, kv => (object?)kv.Value);

    public static async Task<IReadOnlyList<DiagAppointmentRow>> AppointmentsAsync(
        NpgsqlDataSource db, string tenantId, string phone, DateTimeOffset? since, int limit, CancellationToken ct)
    {
        const string sql = """
            SELECT a.id AS Id, a.doctor_id AS DoctorId, d.full_name AS Doctor, p.phone AS Phone, p.full_name AS PatientName,
                   a.starts_at AS StartsAtUtc, a.status AS Status, a.reason AS Reason, a.source AS Source, a.created_at AS CreatedAtUtc
            FROM clinic.appointments a
            JOIN clinic.patients p ON p.id = a.patient_id
            JOIN clinic.doctors  d ON d.id = a.doctor_id
            WHERE a.tenant_id = @tenantId AND p.phone = @phone AND a.created_at >= @since
            ORDER BY a.created_at DESC
            LIMIT @limit
            """;
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<DiagAppointmentRow>(new CommandDefinition(sql,
            new { tenantId, phone, since = (since ?? DateTimeOffset.UnixEpoch).UtcDateTime, limit }, cancellationToken: ct));
        return rows.ToList();
    }

    public static object Describe(DiagAppointmentRow r, TimeZoneInfo tz) => new
    {
        r.Id, r.DoctorId, r.Doctor, r.Phone, r.PatientName,
        startsLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(r.StartsAtUtc, DateTimeKind.Utc), tz).ToString("yyyy-MM-dd HH:mm"),
        r.StartsAtUtc, r.Status, r.Reason, r.Source, r.CreatedAtUtc
    };

    /// <summary>First slot from tomorrow on that Clinic.Api offers AND no other conversation holds in Redis.</summary>
    public static async Task<(ClinicDoctor Doctor, ClinicSlot Slot, DateOnly Date)?> FindFreeSlotAsync(
        ClinicApiClient clinic, BookingHoldService holds, Tenant tenant, int? doctorId, string phone,
        TimeProvider clock, int days, CancellationToken ct)
    {
        var doctors = await clinic.GetDoctorsAsync(tenant.Id, ct);
        var doctor = doctorId is null ? doctors.FirstOrDefault() : doctors.FirstOrDefault(d => d.Id == doctorId);
        if (doctor is null) return null;

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), tenant.TimeZone).DateTime);
        for (var i = 1; i <= days; i++)
        {
            var date = today.AddDays(i);
            var slots = await clinic.GetSlotsAsync(tenant.Id, doctor.Id, date, ct);
            if (slots is null || slots.Slots.Count == 0) continue;
            var held = await holds.GetHeldByOthersAsync(tenant.Id, doctor.Id, slots.Slots.Select(s => s.StartsAt).ToList(), phone);
            var slot = slots.Slots.FirstOrDefault(s => !held.Contains(s.StartsAt));
            if (slot is not null) return (doctor, slot, date);
        }
        return null;
    }
}
