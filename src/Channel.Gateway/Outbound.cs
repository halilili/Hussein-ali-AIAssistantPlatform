using System.Net.Http.Headers;
using Assistant.Contracts;
using Assistant.Core.Tenants;
using MassTransit;
using Microsoft.Extensions.Options;

namespace Channel.Gateway;

/// <summary>Delivers assistant replies to the channel the patient used.</summary>
public sealed class OutboundMessageConsumer(TenantStore tenants, WhatsAppSender whatsApp, ILogger<OutboundMessageConsumer> log)
    : IConsumer<OutboundMessage>
{
    public async Task Consume(ConsumeContext<OutboundMessage> context)
    {
        var m = context.Message;
        switch (m.Channel)
        {
            case ChannelKind.WhatsApp:
                var tenant = await tenants.GetAsync(m.TenantId, context.CancellationToken)
                             ?? throw new InvalidOperationException($"Unknown tenant {m.TenantId}");
                await whatsApp.SendTextAsync(tenant.WaPhoneNumberId!, m.To, m.Text, context.CancellationToken);
                break;

            case ChannelKind.Simulator:
                // The reply is already in the Redis conversation; the simulator UI polls it.
                log.LogInformation("[SIM {Tenant}] → {To}: {Text}", m.TenantId, m.To, m.Text);
                break;

            default:
                log.LogWarning("Channel {Channel} not implemented yet", m.Channel);
                break;
        }
    }
}

/// <summary>
/// WhatsApp Cloud API sender. Free-form text is allowed only inside the 24-hour customer-service window
/// (the patient wrote first). Reminders sent by the clinic later need approved TEMPLATE messages (Lab 17).
/// </summary>
public sealed class WhatsAppSender(HttpClient http, IOptions<WhatsAppOptions> options)
{
    public async Task SendTextAsync(string phoneNumberId, string to, string text, CancellationToken ct)
    {
        var o = options.Value;
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://graph.facebook.com/{o.GraphApiVersion}/{phoneNumberId}/messages")
        {
            Content = JsonContent.Create(new
            {
                messaging_product = "whatsapp",
                to,
                type = "text",
                text = new { preview_url = false, body = text }
            })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.AccessToken);

        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();   // throws → MassTransit retry policy on gateway-outbound
    }
}
