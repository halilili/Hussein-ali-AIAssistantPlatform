using Agent.Worker.Agents;
using Agent.Worker.Booking;
using Agent.Worker.Clients;
using Agent.Worker.Prompts;
using Assistant.Contracts;
using Assistant.Core;
using Assistant.Core.Llm;
using MassTransit;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.SemanticKernel;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = Host.CreateApplicationBuilder(args);
var config = builder.Configuration;

// ---- Shared platform services (Postgres, Redis, tenants, conversations, Qdrant, embeddings)
builder.Services.AddAssistantCore(config);
builder.Services.AddAssistantKnowledge(config);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PromptLibrary>();
builder.Services.AddSingleton<BookingHoldService>();

// ---- Business system client. Retries are DISABLED for POST: retrying a booking after a timeout
//      could book twice (or fail on the slot we just took). GETs are retried.
builder.Services.AddHttpClient<ClinicApiClient>(c => c.BaseAddress = new Uri(config["ClinicApi:BaseUrl"]!))
    .AddStandardResilienceHandler(o => o.Retry.DisableForUnsafeHttpMethods());

// ---- Semantic Kernel: chat completion on the same OpenAI-compatible endpoint (Ollama by default)
var llm = config.GetSection(LlmOptions.Section).Get<LlmOptions>() ?? new LlmOptions();
builder.Services.AddKernel()
    .AddOpenAIChatCompletion(llm.ChatModel, LlmClientFactory.Create(llm));

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

builder.Build().Run();
