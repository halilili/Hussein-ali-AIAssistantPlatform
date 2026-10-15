using Agent.Worker.Booking;
using Agent.Worker.Clients;
using Agent.Worker.Plugins;
using Agent.Worker.Prompts;
using Assistant.Contracts;
using Assistant.Core.Conversations;
using Assistant.Core.Knowledge;
using Assistant.Core.Tenants;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace Agent.Worker.Agents;

/// <summary>
/// One inbound message → one reply. ReAct loop handled by Semantic Kernel auto function calling.
/// Lab 3: system prompt + history only (comment out the two AddFromObject lines).
/// Lab 4: + ClinicPlugin (tools).   Lab 5: + KnowledgePlugin (RAG over Qdrant).
/// </summary>
public sealed class AssistantAgent(
    Kernel kernel,                       // transient: a fresh kernel per message, so per-conversation plugins never leak
    TenantStore tenants,
    PromptLibrary prompts,
    ConversationStore conversations,
    ClinicApiClient clinicApi,
    BookingHoldService holds,
    KnowledgeIndex knowledge,
    TimeProvider clock,
    ILogger<AssistantAgent> log) : IAssistantAgent
{
    private const int HistoryTurns = 12;        // ~6 exchanges; Lab 6 replaces this with token-budgeted trimming + summary

    public async Task<string> HandleAsync(InboundMessage msg, CancellationToken ct)
    {
        using var activity = AgentTelemetry.Source.StartActivity("assistant.handle_message");

        var tenant = await tenants.GetAsync(msg.TenantId, ct)
                     ?? throw new InvalidOperationException($"Unknown tenant {msg.TenantId}");

        // 1) System prompt: versioned template + live tenant data (doctors) + date table
        var doctors = await clinicApi.GetDoctorsAsync(tenant.Id, ct);
        var prompt = prompts.RenderClinicSystemPrompt(tenant, doctors, clock.GetUtcNow());

        activity?.SetTag("tenant.id", tenant.Id);
        activity?.SetTag("channel", msg.Channel.ToString());
        activity?.SetTag("prompt.version", prompt.Version);
        activity?.SetTag("prompt.template_hash", prompt.TemplateHash);
        activity?.SetTag("patient.pseudonym", AgentTelemetry.Pseudonymize(msg.From));

        // 2) Short-term memory
        var history = new ChatHistory(prompt.Text);
        foreach (var turn in await conversations.GetRecentAsync(tenant.Id, msg.From, HistoryTurns))
        {
            if (turn.Role == ChatTurn.User) history.AddUserMessage(turn.Text);
            else history.AddAssistantMessage(turn.Text);
        }
        history.AddUserMessage(msg.Text);

        // 3) Tools bound to THIS conversation (tenant + phone come from the context, not from the model)
        var ctx = new ConversationContext(tenant, msg.From, msg.Channel);
        kernel.Plugins.AddFromObject(new ClinicPlugin(clinicApi, holds, ctx, clock), "clinic");       // Lab 4
        kernel.Plugins.AddFromObject(new KnowledgePlugin(knowledge, ctx), "knowledge");               // Lab 5

        // 4) Generate (the model may call tools several times before answering)
        var settings = new OpenAIPromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(),
            Temperature = 0.2,        // a secretary should be consistent, not creative
            MaxTokens = 500
        };
        var chat = kernel.GetRequiredService<IChatCompletionService>();
        var result = await chat.GetChatMessageContentAsync(history, settings, kernel, ct);
        var reply = string.IsNullOrWhiteSpace(result.Content) ? FallbackReplies.TechnicalIssue : result.Content.Trim();

        // 5) Persist only the user text and the final answer (never tool payloads)
        await conversations.AppendAsync(tenant.Id, msg.From,
            new ChatTurn(ChatTurn.User, msg.Text, msg.ReceivedAt),
            new ChatTurn(ChatTurn.Assistant, reply, clock.GetUtcNow()));

        log.LogInformation("Replied for {Tenant} (prompt {Version}/{Hash}) in {Ms} ms",
            tenant.Id, prompt.Version, prompt.TemplateHash, (clock.GetUtcNow() - msg.ReceivedAt).TotalMilliseconds);
        return reply;
    }
}
