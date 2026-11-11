using Agent.Worker.Agents;
using Agent.Worker.Booking;
using Agent.Worker.Clients;
using Agent.Worker.Diagnostics;
using Agent.Worker.Prompts;
using Assistant.Contracts;
using Assistant.Core;
using Assistant.Core.Llm;
using Assistant.Logging;
using MassTransit;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.SemanticKernel;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// Agent.Worker is a web host since the diagnostics API was added (http://localhost:5300/diag).
// Its main job is still the RabbitMQ consumer; the HTTP endpoints only test the dependencies and the flow.
var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// ---- File logging: <repo>/logs/all-, worker-, redis-, llm-, clinic-YYYYMMDD.log (see Assistant.Logging)
builder.Services.AddLabLogging(config, "worker", builder.Environment.ContentRootPath);

// ---- Shared platform services (Postgres, Redis, tenants, conversations, Qdrant, embeddings)
builder.Services.AddAssistantCore(config);
builder.Services.AddAssistantKnowledge(config);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PromptLibrary>();
builder.Services.AddSingleton<BookingHoldService>();

// ---- Business system client. Retries are DISABLED for POST: retrying a booking after a timeout
//      could book twice (or fail on the slot we just took). GETs are retried.
//      Timeouts: the defaults (10 s per attempt, 30 s total) are too tight when Ollama saturates the CPU on the same
//      machine — a slots call took 87 s and the tool failed (incident, Step 9). Configurable via ClinicApi:TimeoutSeconds.
var clinicTimeout = TimeSpan.FromSeconds(config.GetValue("ClinicApi:TimeoutSeconds", 90));
builder.Services.AddHttpClient<ClinicApiClient>(c => c.BaseAddress = new Uri(config["ClinicApi:BaseUrl"]!))
    .AddStandardResilienceHandler(o =>
    {
        o.Retry.DisableForUnsafeHttpMethods();
        o.AttemptTimeout.Timeout = clinicTimeout / 3;                 // e.g. 30 s per attempt
        o.TotalRequestTimeout.Timeout = clinicTimeout;                // e.g. 90 s including retries
        o.CircuitBreaker.SamplingDuration = clinicTimeout / 3 * 2;    // must be >= 2 x attempt timeout (options validation)
    });

// ---- Semantic Kernel: chat completion on the same OpenAI-compatible endpoint (Ollama by default)
var llm = config.GetSection(LlmOptions.Section).Get<LlmOptions>() ?? new LlmOptions();
builder.Services.AddKernel()
    .AddOpenAIChatCompletion(llm.ChatModel, LlmClientFactory.Create(llm));
builder.Services.AddSingleton<IFunctionInvocationFilter, ToolCallLoggingFilter>();   // every tool call → logs/llm-*.log

// ---- The agent. Lab 2: EchoAgent. Lab 3+: AssistantAgent.
// builder.Services.AddScoped<IAssistantAgent, EchoAgent>();
builder.Services.AddScoped<IAssistantAgent, AssistantAgent>();

// ---- RabbitMQ. Messages of the SAME conversation must be processed in order ("hi" then "I want an
//      appointment"), so the endpoint is partitioned by tenant+phone; different patients run in parallel.
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<InboundMessageConsumer>();
    x.UsingRabbitMq((ctx, cfg) =>
    {
        var rmq = config.GetSection("RabbitMq");
        cfg.Host(rmq["Host"], "/", h => { h.Username(rmq["Username"]!); h.Password(rmq["Password"]!); });
        cfg.ReceiveEndpoint("agent-inbound", e =>
        {
            e.PrefetchCount = 16;
            var partitioner = e.CreatePartitioner(8);
            e.ConfigureConsumer<InboundMessageConsumer>(ctx, c =>
                c.Message<InboundMessage>(m => m.UsePartitioner(partitioner, p => $"{p.Message.TenantId}:{p.Message.From}")));
        });
    });
});

// ---- Observability → Arize Phoenix (OTLP gRPC :4317). Prompt/response content only in Development (PII).
AppContext.SetSwitch("Microsoft.SemanticKernel.Experimental.GenAI.EnableOTelDiagnosticsSensitive", builder.Environment.IsDevelopment());
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("agent-worker"))
    .WithTracing(t => t
        .AddSource("Microsoft.SemanticKernel*", "MassTransit", AgentTelemetry.SourceName)
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(o => o.Endpoint = new Uri(config["Otel:Endpoint"] ?? "http://localhost:4317")));

// ---- Diagnostics API (Redis, Postgres, RabbitMQ, Clinic.Api, Qdrant, LLM, end-to-end flow)
builder.Services.AddHealthChecks();                 // MassTransit adds its bus/endpoint checks to this
builder.Services.AddHttpClient("diag");             // Ollama /v1/models, RabbitMQ management API
builder.Services.AddScoped<FlowTester>();

var app = builder.Build();
app.MapDiagnostics();
app.Run();
