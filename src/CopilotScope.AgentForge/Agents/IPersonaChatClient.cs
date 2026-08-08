namespace CopilotScope.AgentForge.Agents;

/// <summary>Abstraction over the underlying agent/model call, so the API layer and its tests
/// don't depend directly on the Azure AI Foundry SDK.</summary>
public interface IPersonaChatClient
{
    Task<string> ChatAsync(string systemPrompt, string userMessage, CancellationToken ct);
}
