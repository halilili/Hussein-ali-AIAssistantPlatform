namespace Clinic.Api;

// ---- Read models (column order = constructor order for Dapper) ----
public sealed record DoctorDto(int Id, string FullName, string Specialty, int SlotMinutes, decimal ConsultationFeeUsd);

public sealed record WorkingHoursRow(int StartMinute, int EndMinute);

public sealed record AppointmentRow(Guid Id, int DoctorId, string DoctorName, DateTime StartsAtUtc, string Status, string? Reason);

// ---- API contracts ----
public sealed record SlotDto(DateTimeOffset StartsAt, DateTimeOffset EndsAt);

public sealed record SlotsResponse(int DoctorId, DateOnly Date, string TimeZone, IReadOnlyList<SlotDto> Slots);

public sealed record BookAppointmentRequest(int DoctorId, DateTimeOffset StartsAt, string PatientPhone, string PatientName, string? Reason);

public sealed record CancelAppointmentRequest(string PatientPhone);

public sealed record AppointmentDto(Guid Id, int DoctorId, string DoctorName, DateTimeOffset StartsAt, string Status, string? Reason);

/// <summary>Stable machine-readable error codes returned in ProblemDetails.extensions["code"].</summary>
public static class ErrorCodes
{
    public const string NotFound = "not_found";
    public const string SlotTaken = "slot_taken";                 // 409: someone booked it first
    public const string SlotNotAvailable = "slot_not_available";  // 422: outside schedule / in the past
    public const string TooManyUpcoming = "too_many_upcoming";    // 422: anti-spam business rule
    public const string LateCancellation = "late_cancellation";   // 422: < 2h before the appointment
}
