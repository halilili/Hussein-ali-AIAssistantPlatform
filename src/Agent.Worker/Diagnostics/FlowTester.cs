using System.Diagnostics;
using System.Text.Json;
using Agent.Worker.Agents;
using Agent.Worker.Booking;
using Agent.Worker.Clients;
using Assistant.Contracts;
using Assistant.Core.Conversations;
using Assistant.Core.Tenants;
using Assistant.Logging;
using Dapper;
using MassTransit;
using Npgsql;
using StackExchange.Redis;

namespace Agent.Worker.Diagnostics;

/// <param name="Phone">Use a TEST number: with ResetPatient=true its conversation, pending proposal and upcoming
/// appointments are cleared/cancelled first. Default 96170009999.</param>
/// <param name="BookText">Default: a precise request for the first free slot the test found, so a failure points
/// at the platform rather than at the model's date resolution.</param>
public sealed record FlowRequest(
    string? TenantId = null,
    string? Phone = null,
    string? PatientName = null,
    int? DoctorId = null,
    string? BookText = null,
    string? ConfirmText = null,
    bool? ResetPatient = null,
    bool? Cleanup = null,
    int? StepTimeoutSeconds = null);

public sealed record FlowStep(int No, string Step, string Status, double Ms, string Project, string Queue,
    object? Details = null, string? Error = null, string? LookAt = null);

public sealed record FlowReport(bool Ok, string TenantId, string Phone, double TotalMs,
    IReadOnlyList<FlowStep> Steps, IReadOnlyList<ChatTurn> Transcript);

/// <summary>
/// End-to-end booking test through the REAL pipeline, the same path as the simulator:
/// publish InboundMessage → RabbitMQ agent-inbound → InboundMessageConsumer → AssistantAgent (Ollama + tools)
/// → Redis pending/hold → "yes" → confirm_booking → Clinic.Api → Postgres → Redis released → cleanup.
/// Each step reports the project and queue involved and, on failure, the log file to open.
/// </summary>
public sealed class FlowTester(
    IPublishEndpoint bus,
    IConnectionMultiplexer redis,
    NpgsqlDataSource db,
    ConversationStore conversations,
    BookingHoldService holds,
    ClinicApiClient clinic,
    TenantStore tenants,
    TimeProvider clock,
    ILoggerFactory loggers)
{
    public const string DefaultPhone = "96170009999";
    private readonly ILogger _log = loggers.CreateLogger(LogCategories.Flow);
    private readonly List<FlowStep> _steps = [];
    private bool _failed;

    public async Task<FlowReport> RunAsync(FlowRequest req, CancellationToken ct)
    {
        var total = Stopwatch.GetTimestamp();
        var tenantId = req.TenantId ?? "clinic-haddad";
        var phone = req.Phone ?? DefaultPhone;
        var patientName = req.PatientName ?? "Diag Patient";
        var timeout = TimeSpan.FromSeconds(req.StepTimeoutSeconds ?? 240);
        var flowStart = clock.GetUtcNow();
        var rdb = redis.GetDatabase();

        Tenant tenant = null!;
        ClinicDoctor doctor = null!;
        ClinicSlot slot = null!;
        PendingBooking? pending = null;
        DiagAppointmentRow? booked = null;

        _log.LogInformation("FLOW start tenant={Tenant} phone={Phone}", tenantId, phone);

        await Step("Preflight: Redis, Postgres, Clinic.Api, tenant", "Agent.Worker → Redis, Postgres, Clinic.Api", "-", async () =>
        {
            await rdb.PingAsync();
            await using (var conn = await db.OpenConnectionAsync(ct)) await conn.ExecuteScalarAsync<int>("select 1");
            tenant = await tenants.GetAsync(tenantId, ct) ?? throw new InvalidOperationException($"Tenant {tenantId} not found");
            var doctors = await clinic.GetDoctorsAsync(tenantId, ct);
            return new { tenant = tenant.Id, timeZone = tenant.TimeZoneId, doctors = doctors.Count };
        }, "GET /diag/all shows which dependency is down");

        await Step("Reset test patient (conversation, pending proposal, upcoming appointments)", "Agent.Worker → Redis, Clinic.Api", "-", async () =>
        {
            if (req.ResetPatient == false) return new { skipped = "ResetPatient=false" };
            await conversations.ClearAsync(tenantId, phone);
            var oldPending = await holds.GetPendingAsync(tenantId, phone);
            if (oldPending is not null) await holds.ClearPendingAsync(tenantId, phone, oldPending);
            var cancelled = new List<Guid>();
            foreach (var a in await clinic.GetUpcomingAsync(tenantId, phone, ct))
                if (a.Status == "booked" && (await clinic.CancelAsync(tenantId, a.Id, phone, ct)).Ok) cancelled.Add(a.Id);
            return new { conversationCleared = ConversationStore.Key(tenantId, phone), pendingReleased = oldPending is not null, cancelledAppointments = cancelled };
        }, "logs/redis-*.log, logs/clinic-*.log");

        await Step("Pick a free slot (Clinic.Api slots minus Redis holds)", "Agent.Worker → Clinic.Api GET slots, Redis MGET hold:*", "-", async () =>
        {
            var pick = await Diag.FindFreeSlotAsync(clinic, holds, tenant, req.DoctorId, phone, clock, 7, ct)
                       ?? throw new InvalidOperationException("No free slot in the next 7 days");
            (doctor, slot) = (pick.Doctor, pick.Slot);
            return new { doctorId = doctor.Id, doctor = doctor.FullName, date = pick.Date.ToString("yyyy-MM-dd"), local = Local(slot.StartsAt, tenant) };
        }, "GET /diag/clinic");

        var bookText = req.BookText ??
            $"Hi, my name is {patientName}. I want to book an appointment with {doctor?.FullName} on " +
            $"{(slot is null ? "" : TimeZoneInfo.ConvertTime(slot.StartsAt, tenant.TimeZone).ToString("yyyy-MM-dd 'at' HH:mm"))}.";
        long before = 0;

        await Step("Send booking message → RabbitMQ", "Agent.Worker (diag) publishes InboundMessage", "agent-inbound", async () =>
        {
            before = await rdb.ListLengthAsync(ConversationStore.Key(tenantId, phone));
            return await PublishAsync(tenantId, phone, bookText, ct);
        }, "RabbitMQ UI http://localhost:15672 → Queues → agent-inbound");

        await Step("Agent reply #1 (LLM + tools: find_available_slots → propose_booking)",
            "Agent.Worker: InboundMessageConsumer → AssistantAgent → Ollama, Clinic.Api, Redis", "agent-inbound → gateway-outbound", async () =>
        {
            var reply = await WaitForReplyAsync(tenantId, phone, before, timeout, ct);
            return new { assistant = reply };
        }, "logs/llm-*.log (LLM request/reply + TOOL lines), logs/worker-*.log (CONSUME/PUBLISH, errors)");

        await Step("Redis: pending proposal + slot hold (TTL 300 s)", "Agent.Worker → Redis", "-", async () =>
        {
            var pendingKey = BookingHoldService.PendingKey(tenantId, phone);
            pending = await holds.GetPendingAsync(tenantId, phone)
                      ?? throw new InvalidOperationException("No pending proposal: the model did not call propose_booking, or the tool failed (see TOOL lines)");
            var holdKey = BookingHoldService.HoldKey(tenantId, pending.DoctorId, pending.StartsAt);
            var owner = await rdb.StringGetAsync(holdKey);
            if (owner != phone) throw new InvalidOperationException($"Hold {holdKey} owner is '{owner}', expected {phone}");
            return new
            {
                pendingKey, pending, pendingTtlSeconds = (await rdb.KeyTimeToLiveAsync(pendingKey))?.TotalSeconds,
                holdKey, holdOwner = owner.ToString(), holdTtlSeconds = (await rdb.KeyTimeToLiveAsync(holdKey))?.TotalSeconds,
                matchesRequestedSlot = slot is not null && pending.DoctorId == doctor.Id && pending.StartsAt == slot.StartsAt
            };
        }, "logs/redis-*.log (SET hold NX, SET pending), logs/llm-*.log (TOOL clinic-propose_booking)");

        await Step("Send confirmation → RabbitMQ", "Agent.Worker (diag) publishes InboundMessage", "agent-inbound", async () =>
        {
            before = await rdb.ListLengthAsync(ConversationStore.Key(tenantId, phone));
            return await PublishAsync(tenantId, phone, req.ConfirmText ?? "yes confirm", ct);
        }, "RabbitMQ UI → agent-inbound");

        await Step("Agent reply #2 (LLM + tool: confirm_booking → Clinic.Api POST appointments)",
            "Agent.Worker → Ollama → Clinic.Api", "agent-inbound → gateway-outbound", async () =>
        {
            var reply = await WaitForReplyAsync(tenantId, phone, before, timeout, ct);
            return new { assistant = reply };
        }, "logs/llm-*.log (TOOL clinic-confirm_booking), logs/clinic-*.log (POST /appointments status)");

        await Step("Postgres: row in clinic.appointments (status booked)", "Clinic.Api → Postgres", "-", async () =>
        {
            var rows = await Diag.AppointmentsAsync(db, tenantId, phone, flowStart, 5, ct);
            booked = rows.FirstOrDefault(r => r.Status == "booked")
                     ?? throw new InvalidOperationException("No booked row created during this run — confirm_booking was not called or Clinic.Api rejected it");
            return Diag.Describe(booked, tenant.TimeZone);
        }, "logs/clinic-*.log (BOOKED / 409 slot_taken / 422 lines), logs/llm-*.log (TOOL clinic-confirm_booking result)");

        await Step("Redis: pending + hold released after confirm", "Agent.Worker → Redis", "-", async () =>
        {
            var pendingKey = BookingHoldService.PendingKey(tenantId, phone);
            var holdKey = pending is null ? null : BookingHoldService.HoldKey(tenantId, pending.DoctorId, pending.StartsAt);
            var pendingLeft = await rdb.KeyExistsAsync(pendingKey);
            var holdLeft = holdKey is not null && await rdb.KeyExistsAsync(holdKey);
            if (pendingLeft || holdLeft) throw new InvalidOperationException($"Still present: pending={pendingLeft}, hold={holdLeft}");
            return new { pendingKey, pendingExists = pendingLeft, holdKey, holdExists = holdLeft };
        }, "logs/redis-*.log (DEL pending, EVAL release-if-owner)");

        // Runs even after a failure, so a half-finished run never leaves a test booking behind.
        await Step("Cleanup: cancel the test appointment via Clinic.Api", "Agent.Worker → Clinic.Api POST cancel → Postgres", "-", async () =>
        {
            if (req.Cleanup == false) return new { skipped = "Cleanup=false — the appointment stays booked" };
            if (booked is null) return new { skipped = "nothing was booked" };
            var res = await clinic.CancelAsync(tenantId, booked.Id, phone, ct);
            if (!res.Ok) throw new InvalidOperationException($"{res.ErrorCode}: {res.ErrorMessage}");
            return new { cancelled = booked.Id };
        }, "logs/clinic-*.log", always: true);

        var transcript = await conversations.GetRecentAsync(tenantId, phone, 10);
        var ok = _steps.All(s => s.Status != "FAIL");
        var report = new FlowReport(ok, tenantId, phone, Diag.Ms(total), _steps, transcript);
        _log.Log(ok ? LogLevel.Information : LogLevel.Warning, "FLOW end {Result} in {Ms:0} ms: {Summary}",
            ok ? "PASSED" : "FAILED", report.TotalMs, string.Join(" | ", _steps.Select(s => $"{s.No}.{s.Status}")));
        return report;
    }

    private async Task Step(string name, string project, string queue, Func<Task<object?>> body, string lookAt, bool always = false)
    {
        var no = _steps.Count + 1;
        if (_failed && !always)
        {
            _steps.Add(new FlowStep(no, name, "SKIPPED", 0, project, queue));
            _log.LogInformation("FLOW {No}. {Step} → SKIPPED (an earlier step failed)", no, name);
            return;
        }

        var start = Stopwatch.GetTimestamp();
        try
        {
            var details = await body();
            var step = new FlowStep(no, name, "OK", Diag.Ms(start), project, queue, details);
            _steps.Add(step);
            _log.LogInformation("FLOW {No}. {Step} → OK ({Ms:0} ms) {Details}", no, name, step.Ms, JsonSerializer.Serialize(details));
        }
        catch (Exception ex)
        {
            _failed = true;
            var step = new FlowStep(no, name, "FAIL", Diag.Ms(start), project, queue, null, $"{ex.GetType().Name}: {ex.Message}", lookAt);
            _steps.Add(step);
            _log.LogWarning(ex, "FLOW {No}. {Step} → FAIL ({Ms:0} ms). Look at: {LookAt}", no, name, step.Ms, lookAt);
        }
    }

    private async Task<object?> PublishAsync(string tenantId, string phone, string text, CancellationToken ct)
    {
        var msg = new InboundMessage(Guid.NewGuid(), $"diag-{Guid.NewGuid():N}", tenantId, ChannelKind.Simulator, phone, text, DateTimeOffset.UtcNow);
        await bus.Publish(msg, ct);
        return new { msg.MessageId, user = text, queue = "agent-inbound" };
    }

    /// <summary>Polls the conversation list (LLEN, silent) until an assistant turn is appended after the user turn.</summary>
    private async Task<string> WaitForReplyAsync(string tenantId, string phone, long before, TimeSpan timeout, CancellationToken ct)
    {
        var key = ConversationStore.Key(tenantId, phone);
        var rdb = redis.GetDatabase();
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (await rdb.ListLengthAsync(key) >= before + 2)
            {
                var last = JsonSerializer.Deserialize<ChatTurn>((await rdb.ListGetByIndexAsync(key, -1)).ToString())!;
                if (last.Role == ChatTurn.Assistant)
                {
                    if (last.Text == FallbackReplies.TechnicalIssue)
                        throw new InvalidOperationException("Agent failed and sent the technical-issue fallback (exception in logs/worker-*.log)");
                    return last.Text;
                }
            }
            await Task.Delay(1000, ct);
        }
        throw new TimeoutException($"No assistant reply in {key} after {timeout.TotalSeconds:0} s — is the consumer running (agent-inbound consumers > 0)? Is Ollama busy?");
    }

    private static string Local(DateTimeOffset t, Tenant tenant) =>
        TimeZoneInfo.ConvertTime(t, tenant.TimeZone).ToString("yyyy-MM-dd HH:mm");
}
