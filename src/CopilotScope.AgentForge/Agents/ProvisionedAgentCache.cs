using System.Collections.Concurrent;
using CopilotScope.AgentForge.Domain;

namespace CopilotScope.AgentForge.Agents;

public sealed record ProvisionedAgent(PersonaProfile Profile, string SystemPrompt, DateTimeOffset ProvisionedAt);

/// <summary>In-memory only — provisioning a persona never writes anything to disk or Postgres.
/// A process restart (or a DELETE call) clears it, which is also how a revoked cohort stops
/// serving chat traffic immediately.</summary>
public sealed class ProvisionedAgentCache
{
    private readonly ConcurrentDictionary<string, ProvisionedAgent> _agents = new(StringComparer.Ordinal);

    public void Set(string personaId, ProvisionedAgent agent) => _agents[personaId] = agent;

    public bool TryGet(string personaId, out ProvisionedAgent agent) => _agents.TryGetValue(personaId, out agent!);

    public bool Remove(string personaId) => _agents.TryRemove(personaId, out _);
}
