using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Agent.Worker.Clients;

// Anti-corruption layer: the agent owns its own view of the clinic system.
// These records are NOT shared with Clinic.Api — in real life that system belongs to someone else.
public sealed record ClinicDoctor(int Id, string FullName, string Specialty, int SlotMinutes, decimal ConsultationFeeUsd);
public sealed record ClinicSlot(DateTimeOffset StartsAt, DateTimeOffset EndsAt);
public sealed record ClinicSlots(int DoctorId, DateOnly Date, string TimeZone, List<ClinicSlot> Slots);
public sealed record ClinicAppointment(Guid Id, int DoctorId, string DoctorName, DateTimeOffset StartsAt, string Status, string? Reason);

public sealed record ClinicResult<T>(T? Value, string? ErrorCode, string? ErrorMessage)
{
    public bool Ok => ErrorCode is null;
}

public sealed class ClinicApiClient(HttpClient http)
{
    public async Task<IReadOnlyList<ClinicDoctor>> GetDoctorsAsync(string tenantId, CancellationToken ct) =>
        await http.GetFromJsonAsync<List<ClinicDoctor>>($"tenants/{tenantId}/doctors", ct) ?? [];

    public async Task<ClinicSlots?> GetSlotsAsync(string tenantId, int doctorId, DateOnly date, CancellationToken ct)
    {
        using var res = await http.GetAsync($"tenants/{tenantId}/doctors/{doctorId}/slots?date={date:yyyy-MM-dd}", ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<ClinicSlots>(ct);
    }

    public async Task<ClinicResult<ClinicAppointment>> BookAsync(
        string tenantId, int doctorId, DateTimeOffset startsAt, string phone, string name, string? reason, CancellationToken ct)
    {
        using var res = await http.PostAsJsonAsync($"tenants/{tenantId}/appointments",
            new { doctorId, startsAt, patientPhone = phone, patientName = name, reason }, ct);
        return await ToResultAsync<ClinicAppointment>(res, ct);
    }

    public async Task<IReadOnlyList<ClinicAppointment>> GetUpcomingAsync(string tenantId, string phone, CancellationToken ct) =>
        await http.GetFromJsonAsync<List<ClinicAppointment>>($"tenants/{tenantId}/appointments?phone={Uri.EscapeDataString(phone)}", ct) ?? [];

    public async Task<ClinicResult<bool>> CancelAsync(string tenantId, Guid appointmentId, string phone, CancellationToken ct)
    {
        using var res = await http.PostAsJsonAsync($"tenants/{tenantId}/appointments/{appointmentId}/cancel", new { patientPhone = phone }, ct);
        return await ToResultAsync<bool>(res, ct);
    }

    /// <summary>4xx → business error the LLM can explain to the patient; 5xx → exception (retry / fallback).</summary>
    private static async Task<ClinicResult<T>> ToResultAsync<T>(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode)
        {
            if (typeof(T) == typeof(bool))                       // commands without a body (204)
                return new ClinicResult<T>((T)(object)true, null, null);
            return new ClinicResult<T>(await res.Content.ReadFromJsonAsync<T>(ct), null, null);
        }

        if ((int)res.StatusCode is >= 400 and < 500)
        {
            var problem = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
            var code = problem.TryGetProperty("code", out var c) ? c.GetString() : "invalid_request";
            var title = problem.TryGetProperty("title", out var t) ? t.GetString() : res.ReasonPhrase;
            return new ClinicResult<T>(default, code ?? "invalid_request", title);
        }

        res.EnsureSuccessStatusCode();
        throw new InvalidOperationException("Unreachable");
    }
}
