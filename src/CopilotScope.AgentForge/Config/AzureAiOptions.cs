namespace CopilotScope.AgentForge.Config;

/// <summary>Bound from CopilotScope:AgentForge:AzureAI. Not validated at startup — only when a
/// chat is actually attempted — so the health endpoint and profile-preview flow keep working
/// without any Azure credentials configured.</summary>
public sealed class AzureAiOptions
{
    public string? Endpoint { get; set; }
    public string? DeploymentName { get; set; }

    /// <summary>Null → use DefaultAzureCredential (managed identity / az login) instead of a key.</summary>
    public string? ApiKey { get; set; }
}
