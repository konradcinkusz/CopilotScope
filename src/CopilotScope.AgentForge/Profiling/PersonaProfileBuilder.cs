using CopilotScope.AgentForge.Clients;
using CopilotScope.AgentForge.Domain;

namespace CopilotScope.AgentForge.Profiling;

/// <summary>Assembles a PersonaProfile from a cohort's consented sessions. Reads only the
/// session ids the cohort names, via the Collector's public query API — never infers which
/// sessions belong to whom.</summary>
public sealed class PersonaProfileBuilder(ICollectorClient collector)
{
    // Hard cap on grounding exemplars folded into the system prompt (Phase 5), so a large
    // cohort never produces an unbounded prompt.
    private const int MaxExemplars = 40;
    private const int TopToolCount = 5;

    public async Task<PersonaProfile> BuildAsync(PersonaCohort cohort, CancellationToken ct)
    {
        var details = new List<Collector.Api.SessionDetailDto>();
        foreach (var sessionId in cohort.SessionIds)
        {
            var detail = await collector.GetSessionDetailAsync(sessionId, ct);
            if (detail is not null) details.Add(detail);
        }

        var exemplars = details
            .SelectMany(d => d.Transcript
                .Where(t => t.Prompt is not null && t.Response is not null)
                .Select(t => new { t.Time, Score = d.Summary.Quality.Score, t.Prompt, t.Response }))
            .OrderByDescending(t => t.Time)
            .Take(MaxExemplars)
            .Select(t => new ExemplarTurn(t.Prompt!, t.Response!, t.Score))
            .ToList();

        var avgQuality = details.Count > 0
            ? Math.Round(details.Average(d => d.Summary.Quality.Score), 1)
            : 0;

        var commonTools = details
            .SelectMany(d => d.Tools)
            .GroupBy(t => t.Name, StringComparer.Ordinal)
            .OrderByDescending(g => g.Sum(t => t.Calls))
            .Take(TopToolCount)
            .Select(g => g.Key)
            .ToList();

        return new PersonaProfile(
            cohort.PersonaId,
            cohort.DisplayLabel,
            details.Count,
            avgQuality,
            commonTools,
            exemplars);
    }
}
