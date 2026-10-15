using Assistant.Contracts;
using Assistant.Core.Conversations;
using Assistant.Core.Tenants;
using MassTransit;

namespace Channel.Gateway;

public sealed record SimulatorMessage(string From, string Text);

/// <summary>
/// WhatsApp simulator: same pipeline as the real webhook (RabbitMQ → Agent.Worker → reply),
/// without a Meta account. The conversation view reads the same Redis history the agent writes.
/// </summary>
public static class Simulator
{
    public static void MapSimulator(this WebApplication app)
    {
        var sim = app.MapGroup("/simulator/{tenantId}").WithTags("Simulator");

        sim.MapPost("/messages", async (string tenantId, SimulatorMessage m, TenantStore tenants, IPublishEndpoint bus, CancellationToken ct) =>
        {
            if (await tenants.GetAsync(tenantId, ct) is null) return Results.NotFound();
            var msg = new InboundMessage(Guid.NewGuid(), $"sim-{Guid.NewGuid():N}", tenantId, ChannelKind.Simulator,
                m.From, m.Text, DateTimeOffset.UtcNow);
            await bus.Publish(msg, ct);
            return Results.Accepted(value: new { msg.MessageId });
        });

        sim.MapGet("/conversations/{phone}", async (string tenantId, string phone, ConversationStore store) =>
            Results.Ok(await store.GetRecentAsync(tenantId, phone, 50)));

        sim.MapDelete("/conversations/{phone}", async (string tenantId, string phone, ConversationStore store) =>
        {
            await store.ClearAsync(tenantId, phone);
            return Results.NoContent();
        });
    }
}
