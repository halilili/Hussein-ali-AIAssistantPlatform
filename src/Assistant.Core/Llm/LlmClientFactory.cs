using System.ClientModel;
using OpenAI;

namespace Assistant.Core.Llm;

/// <summary>Single place that knows how to reach the model server (Ollama, vLLM, OpenAI, Azure OpenAI v1 endpoint).</summary>
public static class LlmClientFactory
{
    public static OpenAIClient Create(LlmOptions o) =>
        new(new ApiKeyCredential(o.ApiKey), new OpenAIClientOptions
        {
            Endpoint = new Uri(o.Endpoint),
            NetworkTimeout = TimeSpan.FromSeconds(o.TimeoutSeconds)
        });
}
