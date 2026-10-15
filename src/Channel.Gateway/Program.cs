using Assistant.Core;
using Channel.Gateway;
using MassTransit;

// Channel.Gateway — the only service exposed to the internet.
// Job: receive (webhook/simulator) → validate → de-duplicate → publish to RabbitMQ → ACK fast.
//      consume replies → deliver via the right channel.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAssistantCore(builder.Configuration);
builder.Services.Configure<WhatsAppOptions>(builder.Configuration.GetSection(WhatsAppOptions.Section));
builder.Services.AddHttpClient<WhatsAppSender>();

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<OutboundMessageConsumer>();
    x.UsingRabbitMq((ctx, cfg) =>
    {
        var rmq = builder.Configuration.GetSection("RabbitMq");
        cfg.Host(rmq["Host"], "/", h => { h.Username(rmq["Username"]!); h.Password(rmq["Password"]!); });
        cfg.ReceiveEndpoint("gateway-outbound", e =>
        {
            e.UseMessageRetry(r => r.Exponential(5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(2)));
            e.ConfigureConsumer<OutboundMessageConsumer>(ctx);
        });
    });
});

var app = builder.Build();
app.MapWhatsAppWebhook();
app.MapSimulator();
app.MapGet("/health", () => Results.Ok("ok"));
app.Run();
