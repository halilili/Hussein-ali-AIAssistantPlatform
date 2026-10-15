using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Assistant.Contracts;
using Assistant.Core.Tenants;

namespace Agent.Worker.Agents;

/// <summary>Who we are talking to — injected into tools by the host, never supplied by the LLM.</summary>
public sealed record ConversationContext(Tenant Tenant, string PatientPhone, ChannelKind Channel);

/// <summary>Swap point between labs: EchoAgent (Lab 2) → AssistantAgent (Lab 3+).</summary>
public interface IAssistantAgent
{
    Task<string> HandleAsync(InboundMessage message, CancellationToken ct);
}

/// <summary>Lab 2: proves the pipeline (webhook → RabbitMQ → worker → reply) before any AI is involved.</summary>
public sealed class EchoAgent : IAssistantAgent
{
    public Task<string> HandleAsync(InboundMessage message, CancellationToken ct) =>
        Task.FromResult($"[echo:{message.TenantId}] {message.Text}");
}

public static class AgentTelemetry
{
    public const string SourceName = "Assistant.Agent";
    public static readonly ActivitySource Source = new(SourceName);

    /// <summary>Traces carry a stable pseudonym, never the raw phone number (PII).</summary>
    public static string Pseudonymize(string phone) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(phone)))[..12];
}

public static class FallbackReplies
{
    public const string TechnicalIssue =
        "عذراً، عندي مشكلة تقنية حالياً وفريق العيادة رح يتواصل معك قريباً.\n" +
        "Sorry, I'm having a technical issue — the clinic team will get back to you shortly.";
}
