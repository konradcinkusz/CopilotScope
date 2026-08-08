namespace CopilotScope.AgentForge.Domain;

/// <summary>One grounding example folded into the persona's system prompt.</summary>
public sealed record ExemplarTurn(string Prompt, string Response, double SessionQualityScore);

/// <summary>The assembled grounding context for one persona — built fresh from a PersonaCohort's
/// consented sessions each time it is provisioned, never persisted or trained on.</summary>
public sealed record PersonaProfile(
    string PersonaId,
    string DisplayLabel,
    int SessionsUsed,
    double AvgQualityScore,
    List<string> CommonTools,
    List<ExemplarTurn> Exemplars);
