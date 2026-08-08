using CopilotScope.AgentForge.Domain;

namespace CopilotScope.AgentForge.Config;

/// <summary>Bound from CopilotScope:AgentForge:Cohorts — the full, static list of consented personas.</summary>
public sealed class CohortsOptions
{
    public List<PersonaCohort> Cohorts { get; set; } = new();
}
