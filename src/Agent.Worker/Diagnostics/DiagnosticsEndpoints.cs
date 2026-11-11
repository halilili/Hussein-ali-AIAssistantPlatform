using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Agent.Worker.Agents;
using Agent.Worker.Booking;
using Agent.Worker.Clients;
using Assistant.Contracts;
using Assistant.Core.Conversations;
using Assistant.Core.Knowledge;
using Assistant.Core.Llm;
using Assistant.Core.Tenants;
using Assistant.Logging;
using Dapper;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Npgsql;
using OpenAI;
using Qdrant.Client;
using StackExchange.Redis;
using static Qdrant.Client.Grpc.Conditions;

namespace Agent.Worker.Diagnostics;

public sealed record AgentProbe(string Text, string? TenantId = null, string? Phone = null);

/// <summary>
/// Agent.Worker diagnostics API (http://localhost:5300/diag). One endpoint per dependency, plus the whole flow.
/// Every endpoint returns { ok, totalMs, checks[] } with 200 when everything passed and 503 otherwise, and writes
/// one summary line to logs/worker-*.log (category Lab.Flow). Lab-only: in production this sits behind admin auth.
/// </summary>
public static class DiagnosticsEndpoints
{
    private const string DefaultTenant = "clinic-haddad";

    public static void MapDiagnostics(this WebApplication app)
    {
        var diag = app.MapGroup("/diag").WithTags("Diagnostics");
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategories.Flow);

        IResult Respond(DiagReport r)
        {
            if (r.Ok) log.LogInformation("DIAG {Area} OK ({Ms:0} ms): {Summary}", r.Area, r.TotalMs, Diag.Summary(r));
            else log.LogWarning("DIAG {Area} FAILED ({Ms:0} ms): {Summary}", r.Area, r.TotalMs, Diag.Summary(r));
            return Results.Json(r, statusCode: r.Ok ? 200 : 503);
        }

        app.MapGet("/", () => Results.Redirect("/diag")).ExcludeFromDescription();

        diag.MapGet("", () => Results.Ok(new
        {
            service = "Agent.Worker",
            endpoints = new[]
            {
                "GET  /diag/all                       every dependency below (no flow)",
                "GET  /diag/redis                     ping, write/read/delete, key counts",
                "GET  /diag/postgres                  connection, row counts, latest appointments",
                "GET  /diag/rabbitmq                  MassTransit bus health + queue depths (management API)",
                "GET  /diag/clinic?tenantId=          Clinic.Api doctors + first free slot",
                "GET  /diag/qdrant?tenantId=&q=       collection, tenant points, test search",
                "GET  /diag/llm                       models present, chat ping, embedding dims",
                "GET  /diag/llm/tools                 does the chat model call a tool? (function calling)",
                "GET  /diag/state/{tenantId}/{phone}  Redis conversation/pending/holds + DB appointments",
                "POST /diag/agent                     run the agent directly (no RabbitMQ) {text, tenantId?, phone?}",
                "POST /diag/flow                      end-to-end booking through RabbitMQ → LLM → Redis → Clinic.Api → Postgres"
            },
            logs = "<repo>/logs: all-, worker-, gateway-, clinic-, redis-, llm-YYYYMMDD.log"
        }));

        diag.MapGet("/redis", async (IConnectionMultiplexer redis) => Respond(await RedisAsync(redis)));
        diag.MapGet("/postgres", async (NpgsqlDataSource db, CancellationToken ct) => Respond(await PostgresAsync(db, ct)));
        diag.MapGet("/rabbitmq", async (HealthCheckService health, IHttpClientFactory http, IConfiguration config, CancellationToken ct) =>
            Respond(await RabbitAsync(health, http, config, ct)));
        diag.MapGet("/clinic", async (string? tenantId, ClinicApiClient clinic, BookingHoldService holds, TenantStore tenants,
            TimeProvider clock, CancellationToken ct) => Respond(await ClinicAsync(tenantId ?? DefaultTenant, clinic, holds, tenants, clock, ct)));
        diag.MapGet("/qdrant", async (string? tenantId, string? q, QdrantClient qdrant, KnowledgeIndex knowledge,
            IOptions<KnowledgeOptions> ko, CancellationToken ct) => Respond(await QdrantAsync(tenantId ?? DefaultTenant, q, qdrant, knowledge, ko.Value, ct)));
        diag.MapGet("/llm", async (OpenAIClient openAi, EmbeddingService embeddings, IHttpClientFactory http,
            IOptions<LlmOptions> lo, CancellationToken ct) => Respond(await LlmAsync(openAi, embeddings, http, lo.Value, ct)));
        diag.MapGet("/llm/tools", async (Kernel kernel, IOptions<LlmOptions> lo, CancellationToken ct) =>
            Respond(await ToolCallingAsync(kernel, lo.Value, ct)));

        diag.MapGet("/all", async (HttpContext http, CancellationToken ct) =>
        {
            var sp = http.RequestServices;
            var start = Stopwatch.GetTimestamp();
            var lo = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
            var reports = new List<DiagReport>
            {
                await RedisAsync(sp.GetRequiredService<IConnectionMultiplexer>()),
                await PostgresAsync(sp.GetRequiredService<NpgsqlDataSource>(), ct),
                await RabbitAsync(sp.GetRequiredService<HealthCheckService>(), sp.GetRequiredService<IHttpClientFactory>(),
                    sp.GetRequiredService<IConfiguration>(), ct),
                await ClinicAsync(DefaultTenant, sp.GetRequiredService<ClinicApiClient>(), sp.GetRequiredService<BookingHoldService>(),
                    sp.GetRequiredService<TenantStore>(), sp.GetRequiredService<TimeProvider>(), ct),
                await QdrantAsync(DefaultTenant, null, sp.GetRequiredService<QdrantClient>(), sp.GetRequiredService<KnowledgeIndex>(),
                    sp.GetRequiredService<IOptions<KnowledgeOptions>>().Value, ct),
                await LlmAsync(sp.GetRequiredService<OpenAIClient>(), sp.GetRequiredService<EmbeddingService>(),
                    sp.GetRequiredService<IHttpClientFactory>(), lo, ct)
            };
            var checks = reports.SelectMany(r => r.Checks.Select(c => c with { Name = $"{r.Area}: {c.Name}" })).ToList();
            return Respond(Diag.Report("all", start, checks));
        });

        diag.MapGet("/state/{tenantId}/{phone}", async (string tenantId, string phone, IConnectionMultiplexer redis,
            ConversationStore conversations, TenantStore tenants, NpgsqlDataSource db, CancellationToken ct) =>
        {
            var tenant = await tenants.GetAsync(tenantId, ct);
            if (tenant is null) return Results.NotFound(new { error = $"tenant {tenantId} not found" });
            var rdb = redis.GetDatabase();

            var pendingKey = BookingHoldService.PendingKey(tenantId, phone);
            var pending = await rdb.StringGetAsync(pendingKey);
            var pendingTtl = await rdb.KeyTimeToLiveAsync(pendingKey);

            var server = redis.GetServer(redis.GetEndPoints().First());
            var holds = new List<object>();
            await foreach (var key in server.KeysAsync(pattern: $"hold:{tenantId}:*", pageSize: 250))
            {
                var owner = await rdb.StringGetAsync(key);
                var ttl = await rdb.KeyTimeToLiveAsync(key);
                holds.Add(new { key = key.ToString(), owner = owner.ToString(), mine = owner == phone, ttlSeconds = ttl?.TotalSeconds });
            }

            var appointments = await Diag.AppointmentsAsync(db, tenantId, phone, null, 10, ct);
            return Results.Ok(new
            {
                conversation = new { key = ConversationStore.Key(tenantId, phone), turns = await conversations.GetRecentAsync(tenantId, phone, 20) },
                pending = new
                {
                    key = pendingKey,
                    value = pending.HasValue ? JsonSerializer.Deserialize<JsonElement>(pending.ToString()) : (JsonElement?)null,
                    ttlSeconds = pendingTtl?.TotalSeconds,
                    note = pending.HasValue ? "proposal waiting for the patient's yes" : "no pending proposal (never proposed, confirmed, or expired after 300 s)"
                },
                holds,
                appointments = appointments.Select(a => Diag.Describe(a, tenant.TimeZone))
            });
        });

        // Runs the agent in-process: isolates "is the agent/LLM/tools OK?" from "is RabbitMQ/consumer OK?".
        diag.MapPost("/agent", async (AgentProbe probe, IAssistantAgent agent, CancellationToken ct) =>
        {
            var start = Stopwatch.GetTimestamp();
            var msg = new InboundMessage(Guid.NewGuid(), $"diag-{Guid.NewGuid():N}", probe.TenantId ?? DefaultTenant,
                ChannelKind.Simulator, probe.Phone ?? FlowTester.DefaultPhone, probe.Text, DateTimeOffset.UtcNow);
            var check = await Diag.Check("agent.HandleAsync", async () =>
            {
                var reply = await agent.HandleAsync(msg, ct);
                if (reply == FallbackReplies.TechnicalIssue) throw new InvalidOperationException("Agent returned the technical-issue fallback");
                return new { msg.TenantId, phone = msg.From, user = msg.Text, assistant = reply };
            }, "logs/llm-*.log (LLM request/reply, TOOL lines) and logs/worker-*.log");
            return Respond(Diag.Report("agent", start, [check]));
        });

        diag.MapPost("/flow", async (FlowRequest? req, FlowTester flow, CancellationToken ct) =>
        {
            var report = await flow.RunAsync(req ?? new FlowRequest(), ct);
            return Results.Json(report, statusCode: report.Ok ? 200 : 503);
        });
    }

    // ------------------------------------------------------------------ Redis
    private static async Task<DiagReport> RedisAsync(IConnectionMultiplexer redis)
    {
        var start = Stopwatch.GetTimestamp();
        var db = redis.GetDatabase();
        const string portHint = "Lab Redis must be on 127.0.0.1:6380: docker ps → assistant-lab-redis-1 0.0.0.0:6380->6379 (incident: radismm_redis owns 6379)";
        var checks = new List<CheckResult>
        {
            await Diag.Check("connection", async () =>
            {
                var ep = redis.GetEndPoints().First();
                var server = redis.GetServer(ep);
                // INFO is an "admin" command for StackExchange.Redis (needs allowAdmin=true, which also enables FLUSHALL...).
                // We don't enable admin mode for the app; without it the LOADING flag is simply reported as unknown.
                string loading;
                try
                {
                    var persistence = (await server.InfoAsync("persistence")).SelectMany(g => g).ToDictionary(kv => kv.Key, kv => kv.Value);
                    loading = persistence.GetValueOrDefault("loading") ?? "?";
                }
                catch (RedisCommandException)
                {
                    loading = "unknown (INFO needs allowAdmin=true; ping + write below prove the server is serving)";
                }
                if (loading == "1") throw new InvalidOperationException("Redis is LOADING its dataset — wrong instance or still starting");
                return new { endpoint = ep.ToString(), connected = redis.IsConnected, version = server.Version.ToString(), loading };
            }, portHint),
            await Diag.Check("ping", async () => new { latencyMs = (await db.PingAsync()).TotalMilliseconds }, portHint),
            await Diag.Check("write/read/delete", async () =>
            {
                var key = $"diag:{Guid.NewGuid():N}";
                await db.StringSetAsync(key, "ok", TimeSpan.FromSeconds(30));
                var value = await db.StringGetAsync(key);
                var deleted = await db.KeyDeleteAsync(key);
                if (value != "ok") throw new InvalidOperationException($"read back '{value}'");
                return new { key, value = value.ToString(), deleted };
            }),
            await Diag.Check("key counts", async () => new
            {
                conversations = await CountKeysAsync(redis, "conv:*"),
                pending = await CountKeysAsync(redis, "pending:*"),
                holds = await CountKeysAsync(redis, "hold:*"),
                dedup = await CountKeysAsync(redis, "msg:seen:*")
            })
        };
        return Diag.Report("redis", start, checks);
    }

    private static async Task<int> CountKeysAsync(IConnectionMultiplexer redis, string pattern, int cap = 10_000)
    {
        var server = redis.GetServer(redis.GetEndPoints().First());
        var n = 0;
        await foreach (var _ in server.KeysAsync(pattern: pattern, pageSize: 250))   // SCAN, never KEYS
            if (++n >= cap) break;
        return n;
    }

    // ------------------------------------------------------------------ Postgres
    private static async Task<DiagReport> PostgresAsync(NpgsqlDataSource db, CancellationToken ct)
    {
        var start = Stopwatch.GetTimestamp();
        const string hint = "Lab Postgres must be on localhost:5433 (incident: native PostgreSQL owns 5432); user lab / lab_pwd";
        var checks = new List<CheckResult>
        {
            await Diag.Check("connection", async () =>
            {
                await using var conn = await db.OpenConnectionAsync(ct);
                return new
                {
                    server = $"{conn.Host}:{conn.Port}",
                    database = conn.Database,
                    version = await conn.ExecuteScalarAsync<string>("select version()"),
                    now = await conn.ExecuteScalarAsync<DateTime>("select now()")
                };
            }, hint),
            await Diag.Check("row counts", async () =>
            {
                await using var conn = await db.OpenConnectionAsync(ct);
                var row = await conn.QuerySingleAsync("""
                    SELECT (SELECT count(*) FROM platform.tenants)  AS tenants,
                           (SELECT count(*) FROM clinic.doctors)    AS doctors,
                           (SELECT count(*) FROM clinic.patients)   AS patients,
                           (SELECT count(*) FROM clinic.appointments WHERE status = 'booked')    AS booked,
                           (SELECT count(*) FROM clinic.appointments WHERE status = 'cancelled') AS cancelled
                    """);
                return Diag.ToDict((object)row);
            }, "Schema missing? The init scripts in docker/sql run only on an EMPTY volume: docker compose down -v, then up -d"),
            await Diag.Check("latest appointments", async () =>
            {
                await using var conn = await db.OpenConnectionAsync(ct);
                var rows = await conn.QueryAsync("""
                    SELECT a.id, a.tenant_id, a.doctor_id, p.phone, a.starts_at, a.status, a.source, a.created_at
                    FROM clinic.appointments a JOIN clinic.patients p ON p.id = a.patient_id
                    ORDER BY a.created_at DESC LIMIT 5
                    """);
                return rows.Select(r => Diag.ToDict((object)r)).ToList();
            })
        };
        return Diag.Report("postgres", start, checks);
    }

    // ------------------------------------------------------------------ RabbitMQ
    private static async Task<DiagReport> RabbitAsync(HealthCheckService health, IHttpClientFactory httpFactory, IConfiguration config, CancellationToken ct)
    {
        var start = Stopwatch.GetTimestamp();
        var rmq = config.GetSection("RabbitMq");
        var checks = new List<CheckResult>
        {
            await Diag.Check("MassTransit bus health", async () =>
            {
                var report = await health.CheckHealthAsync(ct);
                var entries = report.Entries.Select(e => new { name = e.Key, status = e.Value.Status.ToString(), description = e.Value.Description }).ToList();
                if (report.Status == HealthStatus.Unhealthy)
                    throw new InvalidOperationException("Unhealthy: " + string.Join("; ", entries.Select(e => $"{e.name}={e.status} {e.description}")));
                return new { status = report.Status.ToString(), entries };
            }, "docker compose ps rabbitmq; credentials lab / lab_pwd"),
            await Diag.Check("queues (management API)", async () =>
            {
                var url = (rmq["ManagementUrl"] ?? "http://localhost:15672").TrimEnd('/') + "/api/queues/%2F";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{rmq["Username"]}:{rmq["Password"]}")));
                using var res = await httpFactory.CreateClient("diag").SendAsync(req, ct);
                res.EnsureSuccessStatusCode();
                var json = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
                return json.EnumerateArray()
                    .Select(q => new
                    {
                        name = q.GetProperty("name").GetString(),
                        messages = Num(q, "messages"),
                        ready = Num(q, "messages_ready"),
                        unacked = Num(q, "messages_unacknowledged"),
                        consumers = Num(q, "consumers")
                    })
                    .Where(q => q.name is "agent-inbound" or "gateway-outbound" || q.name!.Contains("error") || q.name.Contains("skipped"))
                    .ToList();
            }, "Management UI http://localhost:15672 (lab / lab_pwd). agent-inbound consumers=0 → Worker not consuming; gateway-outbound consumers=0 → Gateway not running")
        };
        return Diag.Report("rabbitmq", start, checks);
    }

    private static long? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;

    // ------------------------------------------------------------------ Clinic.Api
    private static async Task<DiagReport> ClinicAsync(string tenantId, ClinicApiClient clinic, BookingHoldService holds,
        TenantStore tenants, TimeProvider clock, CancellationToken ct)
    {
        var start = Stopwatch.GetTimestamp();
        const string hint = "Is Clinic.Api running on http://localhost:5100 ? (multiple startup projects in VS)";
        var checks = new List<CheckResult>
        {
            await Diag.Check("GET doctors", async () =>
            {
                var doctors = await clinic.GetDoctorsAsync(tenantId, ct);
                if (doctors.Count == 0) throw new InvalidOperationException($"No doctors for {tenantId}");
                return doctors;
            }, hint),
            await Diag.Check("first free slot (next 7 days, not held in Redis)", async () =>
            {
                var tenant = await tenants.GetAsync(tenantId, ct) ?? throw new InvalidOperationException($"Tenant {tenantId} not found");
                var pick = await Diag.FindFreeSlotAsync(clinic, holds, tenant, null, FlowTester.DefaultPhone, clock, 7, ct)
                           ?? throw new InvalidOperationException("No free slot in the next 7 days");
                return new
                {
                    doctor = pick.Doctor.FullName,
                    date = pick.Date.ToString("yyyy-MM-dd"),
                    local = TimeZoneInfo.ConvertTime(pick.Slot.StartsAt, tenant.TimeZone).ToString("HH:mm"),
                    startsAt = pick.Slot.StartsAt
                };
            }, hint)
        };
        return Diag.Report("clinic", start, checks);
    }

    // ------------------------------------------------------------------ Qdrant (RAG)
    private static async Task<DiagReport> QdrantAsync(string tenantId, string? query, QdrantClient qdrant, KnowledgeIndex knowledge,
        KnowledgeOptions o, CancellationToken ct)
    {
        var start = Stopwatch.GetTimestamp();
        var checks = new List<CheckResult>
        {
            await Diag.Check("collection", async () =>
            {
                var info = await qdrant.GetCollectionInfoAsync(o.Collection, ct);
                var tenantPoints = await qdrant.CountAsync(o.Collection, filter: MatchKeyword("tenant_id", tenantId), exact: true, cancellationToken: ct);
                if (tenantPoints == 0) throw new InvalidOperationException($"0 points for {tenantId}: run Knowledge.Ingestion (profile Ingest)");
                return new { o.Collection, status = info.Status.ToString(), totalPoints = info.PointsCount, tenantId, tenantPoints };
            }, "Qdrant on localhost:6334 (gRPC); dashboard http://localhost:6333/dashboard"),
            await Diag.Check("search", async () =>
            {
                var q = string.IsNullOrWhiteSpace(query) ? "Which insurance companies do you accept?" : query;
                var hits = await knowledge.SearchAsync(tenantId, q, ct);
                if (hits.Count == 0) throw new InvalidOperationException($"No hit above MinScore {o.MinScore} for \"{q}\"");
                return new { query = q, o.TopK, o.MinScore, hits = hits.Select(h => new { h.Source, h.Heading, score = Math.Round(h.Score, 3) }) };
            }, "Try GET /diag/qdrant?q=... in English; the agent translates queries to English (KnowledgePlugin)")
        };
        return Diag.Report("qdrant", start, checks);
    }

    // ------------------------------------------------------------------ LLM (Ollama / OpenAI-compatible)
    private static async Task<DiagReport> LlmAsync(OpenAIClient openAi, EmbeddingService embeddings, IHttpClientFactory httpFactory,
        LlmOptions o, CancellationToken ct)
    {
        var start = Stopwatch.GetTimestamp();
        const string ollamaHint = "docker logs assistant-lab-ollama-1 ; the first call loads the model into RAM (30-90 s on CPU) — run again";
        var checks = new List<CheckResult>
        {
            await Diag.Check("models available", async () =>
            {
                var json = await httpFactory.CreateClient("diag").GetFromJsonAsync<JsonElement>(o.Endpoint.TrimEnd('/') + "/models", ct);
                var ids = json.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString() ?? "").ToList();
                bool Has(string n) => ids.Any(id => id == n || id == n + ":latest");
                var required = new[] { o.ChatModel, o.SmallChatModel, o.EmbeddingModel };
                var missing = required.Where(n => !Has(n)).ToList();
                if (missing.Count > 0) throw new InvalidOperationException($"Missing: {string.Join(", ", missing)} → run scripts/pull-models.sh");
                return new { o.Provider, o.Endpoint, required, available = ids };
            }, "Is Ollama up on localhost:11434 ?"),
            await Diag.Check($"chat ping ({o.ChatModel})", async () =>
            {
                var res = await openAi.GetChatClient(o.ChatModel).CompleteChatAsync(
                    new List<OpenAI.Chat.ChatMessage> { new OpenAI.Chat.UserChatMessage("Reply with exactly one word: OK") },
                    new OpenAI.Chat.ChatCompletionOptions { Temperature = 0f, MaxOutputTokenCount = 10 }, ct);
                var text = res.Value.Content.Count > 0 ? res.Value.Content[0].Text : "";
                return new { model = o.ChatModel, reply = text, inputTokens = res.Value.Usage?.InputTokenCount, outputTokens = res.Value.Usage?.OutputTokenCount };
            }, ollamaHint),
            await Diag.Check($"embedding ({o.EmbeddingModel})", async () =>
            {
                var v = await embeddings.EmbedAsync("مرحبا، بدي موعد / hello, I need an appointment", ct);
                if (v.Length != o.EmbeddingDimensions)
                    throw new InvalidOperationException($"{v.Length} dims, config says {o.EmbeddingDimensions} — Qdrant collection must match");
                return new { model = o.EmbeddingModel, dims = v.Length };
            }, ollamaHint)
        };
        return Diag.Report("llm", start, checks);
    }

    /// <summary>Booking depends on the model emitting tool calls. Small/quantised models sometimes answer in prose instead.</summary>
    private static async Task<DiagReport> ToolCallingAsync(Kernel kernel, LlmOptions o, CancellationToken ct)
    {
        var start = Stopwatch.GetTimestamp();
        var check = await Diag.Check($"function calling ({o.ChatModel})", async () =>
        {
            var calls = new List<string>();
            var fn = KernelFunctionFactory.CreateFromMethod((string city) =>
            {
                calls.Add(city);
                return $"It is 14:05 in {city}.";
            }, functionName: "get_local_time", description: "Returns the current local time in a city.");
            kernel.Plugins.AddFromFunctions("diag", new[] { fn });

            var history = new ChatHistory("You are a test assistant. Always use the available tools to answer.");
            history.AddUserMessage("What time is it now in Beirut?");
            var settings = new OpenAIPromptExecutionSettings { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(), Temperature = 0 };
            var reply = await kernel.GetRequiredService<IChatCompletionService>().GetChatMessageContentAsync(history, settings, kernel, ct);

            if (calls.Count == 0) throw new InvalidOperationException($"The model answered WITHOUT calling the tool: \"{reply.Content}\"");
            return new { toolCalls = calls.Count, arguments = calls, reply = reply.Content };
        }, "Use a tool-capable model (qwen2.5:7b). Tool calls are logged in logs/llm-*.log (Lab.Llm.Tools)");
        return Diag.Report("llm-tools", start, [check]);
    }
}
