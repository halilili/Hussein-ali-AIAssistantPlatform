using Assistant.Contracts;
using MassTransit;

namespace Agent.Worker.Agents;

/// <summary>
/// RabbitMQ consumer. A patient must ALWAYS get an answer, so failures produce a polite fallback
/// instead of a retry storm (re-running an agent can repeat side effects). Lab 11 additionally routes
/// failures to the human-handoff queue.
/// </summary>
public sealed class InboundMessageConsumer(IAssistantAgent agent, ILogger<InboundMessageConsumer> log) : IConsumer<InboundMessage>
{
    public async Task Consume(ConsumeContext<InboundMessage> context)
    {
        var m = context.Message;
        string reply;
        try
        {
            reply = await agent.HandleAsync(m, context.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Agent failed for message {MessageId} (tenant {Tenant})", m.MessageId, m.TenantId);
            reply = FallbackReplies.TechnicalIssue;
        }

        await context.Publish(new OutboundMessage(
            Guid.NewGuid(), m.MessageId, m.TenantId, m.Channel, m.From, reply, DateTimeOffset.UtcNow));
    }
}
