namespace Assistant.Contracts;

/// <summary>Where a message came from / must go back to.</summary>
public enum ChannelKind
{
    Simulator = 0,
    WhatsApp = 1,
    Voice = 2
}

/// <summary>
/// A patient/customer message, normalised by Channel.Gateway. Published to RabbitMQ and consumed by Agent.Worker.
/// </summary>
/// <param name="MessageId">Our id (correlation across services and traces).</param>
/// <param name="ExternalMessageId">Provider id (WhatsApp "wamid...") used for de-duplication.</param>
/// <param name="TenantId">Business the message is addressed to (resolved from the receiving number).</param>
/// <param name="From">Sender address: WhatsApp number in digits only, e.g. 96170123456.</param>
public sealed record InboundMessage(
    Guid MessageId,
    string ExternalMessageId,
    string TenantId,
    ChannelKind Channel,
    string From,
    string Text,
    DateTimeOffset ReceivedAt);

/// <summary>The assistant's reply. Published by Agent.Worker, delivered by Channel.Gateway.</summary>
public sealed record OutboundMessage(
    Guid MessageId,
    Guid InReplyTo,
    string TenantId,
    ChannelKind Channel,
    string To,
    string Text,
    DateTimeOffset CreatedAt);
