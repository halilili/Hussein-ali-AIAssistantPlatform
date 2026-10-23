using Clinic.Api;
using Npgsql;
using Scalar.AspNetCore;

// Clinic.Api — the dummy "existing business system" (a clinic management API).
// The assistant NEVER touches this database directly; it only calls these endpoints as tools.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(builder.Configuration.GetConnectionString("Postgres")!));
builder.Services.AddSingleton<ClinicRepository>();
builder.Services.AddSingleton<SlotCalculator>();
builder.Services.AddSingleton(TimeProvider.System);

var app = builder.Build();
app.MapOpenApi();                 // /openapi/v1.json
app.MapScalarApiReference();      // /scalar  — interactive API explorer
app.MapGet("/", () => Results.Redirect("/scalar")).ExcludeFromDescription();
const int MaxUpcomingPerPatient = 2;                       // business rule: anti-spam / fairness
var minCancellationNotice = TimeSpan.FromHours(2);         // business rule: matches knowledge/policies.md

var tenants = app.MapGroup("/tenants/{tenantId}").WithTags("Clinic");

tenants.MapGet("/doctors", async (string tenantId, ClinicRepository repo) =>
    Results.Ok(await repo.GetDoctorsAsync(tenantId)));

tenants.MapGet("/doctors/{doctorId:int}/slots", async (
    string tenantId, int doctorId, DateOnly date,
    ClinicRepository repo, SlotCalculator calc, TimeProvider clock) =>
{
    var doctor = await repo.GetDoctorAsync(tenantId, doctorId);
    var tzId = await repo.GetTimeZoneIdAsync(tenantId);
    if (doctor is null || tzId is null) return Problem(404, ErrorCodes.NotFound, "Doctor not found");

    var slots = await AvailableSlotsAsync(repo, calc, clock, doctor, TimeZoneInfo.FindSystemTimeZoneById(tzId), date);
    return Results.Ok(new SlotsResponse(doctorId, date, tzId, slots));
});

tenants.MapPost("/appointments", async (
    string tenantId, BookAppointmentRequest req,
    ClinicRepository repo, SlotCalculator calc, TimeProvider clock) =>
{
    var doctor = await repo.GetDoctorAsync(tenantId, req.DoctorId);
    var tzId = await repo.GetTimeZoneIdAsync(tenantId);
    if (doctor is null || tzId is null) return Problem(404, ErrorCodes.NotFound, "Doctor not found");
    if (string.IsNullOrWhiteSpace(req.PatientName) || string.IsNullOrWhiteSpace(req.PatientPhone))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["patient"] = ["Name and phone are required."] });

    // Never trust the caller (the LLM!) about availability: recompute the schedule server-side.
    var tz = TimeZoneInfo.FindSystemTimeZoneById(tzId);
    var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(req.StartsAt, tz).DateTime);
    var slots = await AvailableSlotsAsync(repo, calc, clock, doctor, tz, localDate);
    if (!slots.Any(s => s.StartsAt == req.StartsAt))
        return Problem(422, ErrorCodes.SlotNotAvailable, "This time is not an available slot for the doctor.");

    if (await repo.CountUpcomingAsync(tenantId, req.PatientPhone) >= MaxUpcomingPerPatient)
        return Problem(422, ErrorCodes.TooManyUpcoming, $"A patient can hold at most {MaxUpcomingPerPatient} upcoming appointments.");

    try
    {
        var id = await repo.BookAsync(tenantId, req, doctor.SlotMinutes);
        var dto = new AppointmentDto(id, doctor.Id, doctor.FullName, TimeZoneInfo.ConvertTime(req.StartsAt, tz), "booked", req.Reason);
        return Results.Created($"/tenants/{tenantId}/appointments/{id}", dto);
    }
    catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
    {
        return Problem(409, ErrorCodes.SlotTaken, "The slot was booked by someone else a moment ago.");
    }
});

tenants.MapGet("/appointments", async (string tenantId, string phone, ClinicRepository repo) =>
{
    var tzId = await repo.GetTimeZoneIdAsync(tenantId);
    if (tzId is null) return Problem(404, ErrorCodes.NotFound, "Tenant not found");
    var tz = TimeZoneInfo.FindSystemTimeZoneById(tzId);
    var rows = await repo.GetUpcomingAsync(tenantId, phone);
    return Results.Ok(rows.Select(r => ToDto(r, tz)));
});

tenants.MapPost("/appointments/{id:guid}/cancel", async (
    string tenantId, Guid id, CancelAppointmentRequest req, ClinicRepository repo, TimeProvider clock) =>
{
    var appt = await repo.GetForPatientAsync(tenantId, id, req.PatientPhone);      // ownership check
    if (appt is null || appt.Status != "booked") return Problem(404, ErrorCodes.NotFound, "Appointment not found");

    var startsAt = ClinicRepository.ToUtcOffset(appt.StartsAtUtc);
    if (startsAt - clock.GetUtcNow() < minCancellationNotice)
        return Problem(422, ErrorCodes.LateCancellation, "Appointments can be cancelled by WhatsApp up to 2 hours before. Please call the clinic.");

    await repo.CancelAsync(id);
    return Results.NoContent();
});

app.Run();

static async Task<IReadOnlyList<SlotDto>> AvailableSlotsAsync(
    ClinicRepository repo, SlotCalculator calc, TimeProvider clock, DoctorDto doctor, TimeZoneInfo tz, DateOnly date)
{
    var hours = await repo.GetWorkingHoursAsync(doctor.Id, date.DayOfWeek);
    var (fromUtc, toUtc) = SlotCalculator.DayRangeUtc(date, tz);
    var booked = await repo.GetBookedStartsAsync(doctor.Id, fromUtc, toUtc);
    return calc.Calculate(date, tz, doctor.SlotMinutes, hours, booked, clock.GetUtcNow());
}

static AppointmentDto ToDto(AppointmentRow r, TimeZoneInfo tz) =>
    new(r.Id, r.DoctorId, r.DoctorName, TimeZoneInfo.ConvertTime(ClinicRepository.ToUtcOffset(r.StartsAtUtc), tz), r.Status, r.Reason);

static IResult Problem(int status, string code, string title) =>
    Results.Problem(title: title, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });
