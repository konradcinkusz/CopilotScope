using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using CopilotScope.AgentForge.Config;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace CopilotScope.AgentForge.Agents;

/// <summary>
/// Talks to a model deployment hosted in Azure AI Foundry via the Azure OpenAI-compatible
/// endpoint, wrapped as a Microsoft Agent Framework AIAgent. The agent (and its instructions)
/// are built fresh per call from the caller-supplied system prompt — the persona's "identity"
/// lives entirely in that prompt (see PersonaPromptBuilder), not in any stored model state.
///
/// NuGet package names/versions used here (Microsoft.Agents.AI, Azure.AI.OpenAI, Azure.Identity)
/// were verified against nuget.org on 2026-08-08 (see AF-050 in docs/AGENTFORGE.md's linked
/// implementation plan) — this space moves quickly, re-verify before bumping versions.
/// </summary>
public sealed class AzureFoundryPersonaChatClient(AzureAiOptions options) : IPersonaChatClient
{
    public async Task<string> ChatAsync(string systemPrompt, string userMessage, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(options.Endpoint) || string.IsNullOrEmpty(options.DeploymentName))
        {
            throw new InvalidOperationException(
                "AgentForge Azure AI is not configured. Set CopilotScope:AgentForge:AzureAI:Endpoint " +
                "and CopilotScope:AgentForge:AzureAI:DeploymentName before provisioning a persona.");
        }

        var azureClient = string.IsNullOrEmpty(options.ApiKey)
            ? new AzureOpenAIClient(new Uri(options.Endpoint), new DefaultAzureCredential())
            : new AzureOpenAIClient(new Uri(options.Endpoint), new AzureKeyCredential(options.ApiKey));

        IChatClient chatClient = azureClient
            .GetChatClient(options.DeploymentName)
            .AsIChatClient();

        AIAgent agent = chatClient.AsAIAgent(name: "AgentForgePersona", instructions: systemPrompt);

        var response = await agent.RunAsync(userMessage, cancellationToken: ct);
        return response.Text;
    }
}
