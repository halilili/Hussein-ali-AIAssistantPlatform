using Dapper;
using Npgsql;

namespace Clinic.Api;

/// <summary>Plain SQL via Dapper: the queries are the documentation of the business rules.</summary>
public sealed class ClinicRepository(NpgsqlDataSource db)
{
    /// <summary>Active tenants (admin/testing endpoint; the Gateway resolves tenants from the WhatsApp number).</summary>
    public async Task<IReadOnlyList<TenantDto>> GetTenantsAsync()
    {
        await using var c = await db.OpenConnectionAsync();
        var rows = await c.QueryAsync<TenantDto>("""
            SELECT id AS Id, display_name AS DisplayName, vertical AS Vertical, timezone AS TimeZone
            FROM platform.tenants WHERE is_active ORDER BY id
            """);
        return rows.AsList();
    }

    public async Task<string?> GetTimeZoneIdAsync(string tenantId)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<string?>(
            "SELECT timezone FROM platform.tenants WHERE id = @tenantId AND is_active", new { tenantId });
    }

    public async Task<IReadOnlyList<DoctorDto>> GetDoctorsAsync(string tenantId)
    {
        await using var c = await db.OpenConnectionAsync();
        var rows = await c.QueryAsync<DoctorDto>("""
            SELECT id AS Id, full_name AS FullName, specialty AS Specialty,
                   slot_minutes AS SlotMinutes, consultation_fee_usd AS ConsultationFeeUsd
            FROM clinic.doctors WHERE tenant_id = @tenantId ORDER BY id
            """, new { tenantId });
        return rows.AsList();
    }

    public async Task<DoctorDto?> GetDoctorAsync(string tenantId, int doctorId) =>
        (await GetDoctorsAsync(tenantId)).FirstOrDefault(d => d.Id == doctorId);

    public async Task<IReadOnlyList<WorkingHoursRow>> GetWorkingHoursAsync(int doctorId, DayOfWeek weekday)
    {
        await using var c = await db.OpenConnectionAsync();
        var rows = await c.QueryAsync<WorkingHoursRow>("""
            SELECT (EXTRACT(EPOCH FROM start_time) / 60)::int AS StartMinute,
                   (EXTRACT(EPOCH FROM end_time)   / 60)::int AS EndMinute
            FROM clinic.working_hours WHERE doctor_id = @doctorId AND weekday = @weekday
            """, new { doctorId, weekday = (short)weekday });
        return rows.AsList();
    }

    public async Task<HashSet<DateTimeOffset>> GetBookedStartsAsync(int doctorId, DateTime fromUtc, DateTime toUtc)
    {
        await using var c = await db.OpenConnectionAsync();
        var rows = await c.QueryAsync<DateTime>("""
            SELECT starts_at FROM clinic.appointments
            WHERE doctor_id = @doctorId AND status = 'booked' AND starts_at >= @fromUtc AND starts_at < @toUtc
            """, new { doctorId, fromUtc, toUtc });
        return rows.Select(ToUtcOffset).ToHashSet();
    }

    public async Task<long> CountUpcomingAsync(string tenantId, string phone)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<long>("""
            SELECT count(*) FROM clinic.appointments a
            JOIN clinic.patients p ON p.id = a.patient_id
            WHERE a.tenant_id = @tenantId AND p.phone = @phone AND a.status = 'booked' AND a.starts_at > now()
            """, new { tenantId, phone });
    }

    /// <summary>
    /// Upserts the patient and inserts the appointment in one transaction.
    /// Throws PostgresException 23505 when the partial unique index detects a double booking.
    /// </summary>
    public async Task<Guid> BookAsync(string tenantId, BookAppointmentRequest r, int slotMinutes)
    {
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();

        var patientId = await c.ExecuteScalarAsync<int>("""
            INSERT INTO clinic.patients (tenant_id, phone, full_name) VALUES (@tenantId, @phone, @name)
            ON CONFLICT (tenant_id, phone) DO UPDATE SET full_name = EXCLUDED.full_name
            RETURNING id
            """, new { tenantId, phone = r.PatientPhone, name = r.PatientName }, tx);

        var id = await c.ExecuteScalarAsync<Guid>("""
            INSERT INTO clinic.appointments (tenant_id, doctor_id, patient_id, starts_at, ends_at, reason, source)
            VALUES (@tenantId, @doctorId, @patientId, @startsUtc, @endsUtc, @reason, 'whatsapp')
            RETURNING id
            """, new
        {
            tenantId,
            doctorId = r.DoctorId,
            patientId,
            startsUtc = r.StartsAt.UtcDateTime,                    // Npgsql: timestamptz requires UTC
            endsUtc = r.StartsAt.AddMinutes(slotMinutes).UtcDateTime,
            reason = r.Reason
        }, tx);

        await tx.CommitAsync();
        return id;
    }

    public async Task<IReadOnlyList<AppointmentRow>> GetUpcomingAsync(string tenantId, string phone)
    {
        await using var c = await db.OpenConnectionAsync();
        var rows = await c.QueryAsync<AppointmentRow>("""
            SELECT a.id AS Id, a.doctor_id AS DoctorId, d.full_name AS DoctorName,
                   a.starts_at AS StartsAtUtc, a.status AS Status, a.reason AS Reason
            FROM clinic.appointments a
            JOIN clinic.patients p ON p.id = a.patient_id
            JOIN clinic.doctors  d ON d.id = a.doctor_id
            WHERE a.tenant_id = @tenantId AND p.phone = @phone AND a.status = 'booked' AND a.starts_at > now()
            ORDER BY a.starts_at
            """, new { tenantId, phone });
        return rows.AsList();
    }

    public async Task<AppointmentRow?> GetForPatientAsync(string tenantId, Guid id, string phone)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.QuerySingleOrDefaultAsync<AppointmentRow>("""
            SELECT a.id AS Id, a.doctor_id AS DoctorId, d.full_name AS DoctorName,
                   a.starts_at AS StartsAtUtc, a.status AS Status, a.reason AS Reason
            FROM clinic.appointments a
            JOIN clinic.patients p ON p.id = a.patient_id
            JOIN clinic.doctors  d ON d.id = a.doctor_id
            WHERE a.tenant_id = @tenantId AND a.id = @id AND p.phone = @phone
            """, new { tenantId, id, phone });
    }

    public async Task CancelAsync(Guid id)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync("UPDATE clinic.appointments SET status = 'cancelled' WHERE id = @id AND status = 'booked'", new { id });
    }

    public static DateTimeOffset ToUtcOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
