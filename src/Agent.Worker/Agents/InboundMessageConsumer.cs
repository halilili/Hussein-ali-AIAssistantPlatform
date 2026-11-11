using System.Diagnostics;
using Assistant.Contracts;
using Assistant.Core.Conversations;
using MassTransit;

namespace Agent.Worker.Agents;

/// <summary>
/// RabbitMQ consumer. A patient must ALWAYS get an answer, so failures produce a polite fallback
/// instead of a retry storm (re-running an agent can repeat side effects). Lab 11 additionally routes
/// failures to the human-handoff queue.
/// </summary>
/// <remarks>
/// On failure the user message and the fallback are written to the conversation too: the agent only persists
/// history on success, so without this the simulator showed an empty conversation (e.g. when Clinic.Api was down)
/// and the secretary would never see that the patient wrote.
/// </remarks>
public sealed class InboundMessageConsumer(
    IAssistantAgent agent,
    ConversationStore conversations,
    ILogger<InboundMessageConsumer> log) : IConsumer<InboundMessage>
{
    public async Task Consume(ConsumeContext<InboundMessage> context)
    {
        var m = context.Message;
        var start = Stopwatch.GetTimestamp();
        log.LogInformation("CONSUME agent-inbound {MessageId} {Tenant}/{From} via {Channel}: \"{Text}\"",
            m.MessageId, m.TenantId, m.From, m.Channel, m.Text);
        string reply;
        try
        {
            reply = await agent.HandleAsync(m, context.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Agent failed for message {MessageId} (tenant {Tenant})", m.MessageId, m.TenantId);
            reply = FallbackReplies.TechnicalIssue;
            try
            {
                await conversations.AppendAsync(m.TenantId, m.From,
                    new ChatTurn(ChatTurn.User, m.Text, m.ReceivedAt),
                    new ChatTurn(ChatTurn.Assistant, reply, DateTimeOffset.UtcNow));
            }
            catch (Exception storeEx)
            {
                log.LogWarning(storeEx, "Could not store the fallback turn for message {MessageId}", m.MessageId);
            }
        }

        var outbound = new OutboundMessage(
            Guid.NewGuid(), m.MessageId, m.TenantId, m.Channel, m.From, reply, DateTimeOffset.UtcNow);
        await context.Publish(outbound);
        log.LogInformation("PUBLISH OutboundMessage {OutId} (reply to {MessageId}) → gateway-outbound, {Ms:0} ms end-to-end in worker",
            outbound.MessageId, m.MessageId, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }
}
