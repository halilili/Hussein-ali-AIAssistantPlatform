using System.ComponentModel;
using System.Text;
using Agent.Worker.Agents;
using Assistant.Core.Knowledge;
using Microsoft.SemanticKernel;

namespace Agent.Worker.Plugins;

/// <summary>
/// Agentic RAG: retrieval is a tool the model calls when it needs clinic facts.
/// Booking turns ("eh, confirm") skip retrieval entirely → fewer tokens and lower latency than
/// retrieving on every message. Tenant isolation is enforced here, not by the model.
/// </summary>
public sealed class KnowledgePlugin(KnowledgeIndex index, ConversationContext ctx)
{
    [KernelFunction("search_clinic_knowledge")]
    [Description("Searches the clinic's official knowledge base: test prices, payment methods, insurance, location, parking, opening hours, test preparation, cancellation and other policies. Use it for ANY factual question about the clinic before answering.")]
    public async Task<string> SearchAsync(
        [Description("Short search query, e.g. 'insurance accepted', 'parking', 'stress test preparation'. Any language.")] string query,
        CancellationToken ct)
    {
        var hits = await index.SearchAsync(ctx.Tenant.Id, query, ct);
        if (hits.Count == 0)
            return "NO_RESULTS: the knowledge base has no answer. Tell the patient you will check with the clinic team. Do not guess.";

        var sb = new StringBuilder("<context>\n");
        foreach (var h in hits)
            sb.Append("<chunk source=\"").Append(h.Source).Append("\" score=\"").Append(h.Score.ToString("0.00"))
              .Append("\">\n").Append(h.Text).Append("\n</chunk>\n");
        sb.Append("</context>\nAnswer ONLY from the context above, in the patient's language. If it does not contain the answer, say you will check with the clinic team.");
        return sb.ToString();
    }
}
