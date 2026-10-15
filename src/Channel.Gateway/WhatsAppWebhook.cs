using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assistant.Contracts;
using Assistant.Core.Messaging;
using Assistant.Core.Tenants;
using MassTransit;
using Microsoft.Extensions.Options;

namespace Channel.Gateway;

public sealed class WhatsAppOptions
{
    public const string Section = "WhatsApp";
    public string VerifyToken { get; set; } = "dev-verify-token";   // you choose it; typed into the Meta dashboard
    public string AppSecret { get; set; } = "";                      // Meta app secret → signature validation
    public string AccessToken { get; set; } = "";                    // system-user token for sending
    public string GraphApiVersion { get; set; } = "v21.0";
}

/// <summary>
/// WhatsApp Cloud API webhook. Meta expects a 200 within seconds and retries otherwise,
/// so this endpoint does NO AI work: it validates, de-duplicates and publishes.
/// </summary>
public static class WhatsAppWebhook
{
    public static void MapWhatsAppWebhook(this WebApplication app)
    {
        // 1) One-time subscription handshake from the Meta dashboard
        app.MapGet("/webhooks/whatsapp", (HttpRequest req, IOptions<WhatsAppOptions> opt) =>
        {
            var q = req.Query;
            return q["hub.mode"] == "subscribe" && q["hub.verify_token"] == opt.Value.VerifyToken
                ? Results.Text(q["hub.challenge"].ToString())
                : Results.StatusCode(StatusCodes.Status403Forbidden);
        });

        // 2) Message notifications
        app.MapPost("/webhooks/whatsapp", async (
            HttpRequest req, IOptions<WhatsAppOptions> opt, TenantStore tenants,
            MessageDeduplicator dedup, IPublishEndpoint bus, ILoggerFactory lf, CancellationToken ct) =>
        {
            var log = lf.CreateLogger("WhatsAppWebhook");
            using var reader = new StreamReader(req.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(ct);

            if (!IsSignatureValid(body, req.Headers["X-Hub-Signature-256"].ToString(), opt.Value.AppSecret, log))
                return Results.Unauthorized();

            using var doc = JsonDocument.Parse(body);
            foreach (var entry in doc.RootElement.GetProperty("entry").EnumerateArray())
            foreach (var change in entry.GetProperty("changes").EnumerateArray())
            {
                var value = change.GetProperty("value");
                if (!value.TryGetProperty("messages", out var messages)) continue;   // delivery/read status callbacks

                var phoneNumberId = value.GetProperty("metadata").GetProperty("phone_number_id").GetString()!;
                var tenant = await tenants.FindByWhatsAppNumberAsync(phoneNumberId, ct);
                if (tenant is null) { log.LogWarning("No tenant for phone_number_id {Id}", phoneNumberId); continue; }

                foreach (var m in messages.EnumerateArray())
                {
                    var waMessageId = m.GetProperty("id").GetString()!;
                    if (!await dedup.TryClaimAsync(waMessageId)) continue;           // Meta retry → already handled

                    var text = m.GetProperty("type").GetString() == "text"
                        ? m.GetProperty("text").GetProperty("body").GetString() ?? ""
                        : "[non-text message]";                                    // Lab 17: audio → STT, images

                    await bus.Publish(new InboundMessage(
                        Guid.NewGuid(), waMessageId, tenant.Id, ChannelKind.WhatsApp,
                        m.GetProperty("from").GetString()!, text, DateTimeOffset.UtcNow), ct);
                }
            }
            return Results.Ok();
        });
    }

    /// <summary>X-Hub-Signature-256 = "sha256=" + HMAC-SHA256(appSecret, rawBody). Constant-time compare.</summary>
    private static bool IsSignatureValid(string body, string header, string appSecret, ILogger log)
    {
        if (string.IsNullOrEmpty(appSecret))
        {
            log.LogWarning("WhatsApp:AppSecret not set — signature validation SKIPPED (dev only).");
            return true;
        }
        if (!header.StartsWith("sha256=", StringComparison.Ordinal)) return false;

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), Encoding.UTF8.GetBytes(body));
        try
        {
            var received = Convert.FromHexString(header["sha256=".Length..]);
            return CryptographicOperations.FixedTimeEquals(expected, received);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
