using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Agent.Worker.Agents;
using Agent.Worker.Booking;
using Agent.Worker.Clients;
using Microsoft.SemanticKernel;

namespace Agent.Worker.Plugins;

/// <summary>
/// Tools the LLM may call. Design rules:
///  • The patient's phone comes from ConversationContext, NEVER from an LLM argument (no impersonation).
///  • Every tool returns compact JSON with either data or {error, hint} — the hint tells the model what to do next.
///  • Times are exchanged as clinic-local "yyyy-MM-dd" + "HH:mm"; the plugin owns all time-zone math.
///  • The LLM never sees GUIDs: appointments are referenced by a short 8-char ref.
/// </summary>
public sealed class ClinicPlugin(ClinicApiClient api, BookingHoldService holds, ConversationContext ctx, TimeProvider clock)
{
    private const int MaxSlotsShown = 6;
    private const int BookingHorizonDays = 60;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private TimeZoneInfo Tz => ctx.Tenant.TimeZone;

    [KernelFunction("list_doctors")]
    [Description("Lists the clinic's doctors: id, name, specialty, appointment length in minutes and consultation fee in USD.")]
    public async Task<string> ListDoctorsAsync(CancellationToken ct)
    {
        var doctors = await api.GetDoctorsAsync(ctx.Tenant.Id, ct);
        return Serialize(doctors.Select(d => new { d.Id, d.FullName, d.Specialty, minutes = d.SlotMinutes, feeUsd = d.ConsultationFeeUsd }));
    }

    [KernelFunction("find_available_slots")]
    [Description("Returns free appointment times for one doctor on one date. Always call this before proposing a time.")]
    public async Task<string> FindAvailableSlotsAsync(
        [Description("Doctor id from the doctors list")] int doctorId,
        [Description("Date in yyyy-MM-dd (clinic local). Resolve 'tomorrow', 'bukra', 'next Monday' using the DATE CONTEXT table.")] string date,
        CancellationToken ct)
    {
        if (!TryParseDate(date, out var day)) return Error("invalid_date", "Use yyyy-MM-dd from the DATE CONTEXT table.");
        var today = LocalToday();
        if (day < today) return Error("date_in_past", "Ask the patient for a future date.");
        if (day > today.AddDays(BookingHorizonDays)) return Error("too_far_ahead", $"Bookings open {BookingHorizonDays} days ahead.");

        var result = await api.GetSlotsAsync(ctx.Tenant.Id, doctorId, day, ct);
        if (result is null) return Error("unknown_doctor", "Call list_doctors to get valid ids.");

        var held = await holds.GetHeldByOthersAsync(ctx.Tenant.Id, doctorId, result.Slots.Select(s => s.StartsAt).ToList(), ctx.PatientPhone);
        var free = result.Slots.Where(s => !held.Contains(s.StartsAt)).ToList();

        if (free.Count == 0)
            return Serialize(new { date, weekday = day.DayOfWeek.ToString(), available = false, hint = "No free time that day (or the doctor does not work that day). Offer to check another day." });

        return Serialize(new
        {
            date,
            weekday = day.DayOfWeek.ToString(),
            available = true,
            times = free.Take(MaxSlotsShown).Select(s => LocalTime(s.StartsAt)),
            totalFree = free.Count
        });
    }

    [KernelFunction("propose_booking")]
    [Description("Step 1 of booking: holds the slot for 5 minutes and returns a summary. Read the summary to the patient and ask for confirmation. Requires the patient's full name.")]
    public async Task<string> ProposeBookingAsync(
        [Description("Doctor id")] int doctorId,
        [Description("Date yyyy-MM-dd")] string date,
        [Description("Time HH:mm exactly as returned by find_available_slots")] string time,
        [Description("Patient full name as given by the patient")] string patientName,
        [Description("Short reason for the visit, if the patient gave one")] string? reason = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(patientName) || patientName.Trim().Length < 3)
            return Error("name_required", "Ask the patient for their full name first.");
        if (!TryParseDate(date, out var day) || !TimeOnly.TryParseExact(time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            return Error("invalid_date_or_time", "Use yyyy-MM-dd and HH:mm.");

        var local = day.ToDateTime(t);
        var startsAt = new DateTimeOffset(local, Tz.GetUtcOffset(local));

        var slots = await api.GetSlotsAsync(ctx.Tenant.Id, doctorId, day, ct);
        if (slots is null) return Error("unknown_doctor", "Call list_doctors to get valid ids.");
        if (!slots.Slots.Any(s => s.StartsAt == startsAt))
            return Error("slot_not_available", "Call find_available_slots and offer the patient other times.");

        if (!await holds.TryHoldAsync(ctx.Tenant.Id, doctorId, startsAt, ctx.PatientPhone))
            return Error("slot_held", "Another patient is confirming this time right now. Offer a different time.");

        var doctor = (await api.GetDoctorsAsync(ctx.Tenant.Id, ct)).First(d => d.Id == doctorId);
        var pending = new PendingBooking(doctorId, doctor.FullName, startsAt, patientName.Trim(), reason);
        await holds.SavePendingAsync(ctx.Tenant.Id, ctx.PatientPhone, pending);

        return Serialize(new
        {
            status = "awaiting_confirmation",
            doctor = doctor.FullName,
            when = LocalDateTime(startsAt),
            patientName = pending.PatientName,
            reason,
            heldForMinutes = (int)BookingHoldService.HoldTtl.TotalMinutes,
            hint = "Read this back to the patient and ask them to confirm. Call confirm_booking ONLY after an explicit yes."
        });
    }

    [KernelFunction("confirm_booking")]
    [Description("Step 2 of booking: books the slot from the last proposal. Call ONLY after the patient explicitly confirmed (yes / eh / نعم / oui / ok).")]
    public async Task<string> ConfirmBookingAsync(CancellationToken ct)
    {
        var pending = await holds.GetPendingAsync(ctx.Tenant.Id, ctx.PatientPhone);
        if (pending is null)
            return Error("no_pending_booking", "The 5-minute hold expired or nothing was proposed. Check availability again.");

        var result = await api.BookAsync(ctx.Tenant.Id, pending.DoctorId, pending.StartsAt, ctx.PatientPhone, pending.PatientName, pending.Reason, ct);
        await holds.ClearPendingAsync(ctx.Tenant.Id, ctx.PatientPhone, pending);     // consumed exactly once

        if (!result.Ok)
            return Error(result.ErrorCode!, result.ErrorMessage ?? "Booking failed. Offer another time.");

        return Serialize(new
        {
            status = "booked",
            reference = ShortRef(result.Value!.Id),
            doctor = pending.DoctorName,
            when = LocalDateTime(pending.StartsAt),
            patientName = pending.PatientName,
            hint = "Confirm to the patient with the reference, date, time and the clinic address."
        });
    }

    [KernelFunction("get_my_appointments")]
    [Description("Lists the upcoming appointments of the patient in this chat.")]
    public async Task<string> GetMyAppointmentsAsync(CancellationToken ct)
    {
        var items = await api.GetUpcomingAsync(ctx.Tenant.Id, ctx.PatientPhone, ct);
        return Serialize(new
        {
            count = items.Count,
            appointments = items.Select(a => new { reference = ShortRef(a.Id), doctor = a.DoctorName, when = LocalDateTime(a.StartsAt), a.Reason })
        });
    }

    [KernelFunction("cancel_appointment")]
    [Description("Cancels one of the patient's upcoming appointments. Confirm with the patient which one before calling.")]
    public async Task<string> CancelAppointmentAsync(
        [Description("The 8-character reference from get_my_appointments")] string reference,
        CancellationToken ct)
    {
        var items = await api.GetUpcomingAsync(ctx.Tenant.Id, ctx.PatientPhone, ct);
        var match = items.FirstOrDefault(a => ShortRef(a.Id).Equals(reference.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is null) return Error("not_found", "Call get_my_appointments and use one of its references.");

        var result = await api.CancelAsync(ctx.Tenant.Id, match.Id, ctx.PatientPhone, ct);
        return result.Ok
            ? Serialize(new { status = "cancelled", reference = ShortRef(match.Id), doctor = match.DoctorName, when = LocalDateTime(match.StartsAt) })
            : Error(result.ErrorCode!, result.ErrorMessage ?? "Cancellation failed.");
    }

    // ---- helpers ----
    private DateOnly LocalToday() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), Tz).DateTime);
    private string LocalTime(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, Tz).ToString("HH:mm", CultureInfo.InvariantCulture);
    private string LocalDateTime(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, Tz).ToString("dddd yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    private static string ShortRef(Guid id) => id.ToString("N")[..8].ToUpperInvariant();
    private static bool TryParseDate(string s, out DateOnly d) =>
        DateOnly.TryParseExact(s?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d);
    private static string Serialize(object o) => JsonSerializer.Serialize(o, Json);
    private static string Error(string code, string hint) => Serialize(new { error = code, hint });
}
