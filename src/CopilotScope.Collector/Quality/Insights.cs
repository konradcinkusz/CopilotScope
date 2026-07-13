using CopilotScope.Collector.Domain;

namespace CopilotScope.Collector.Quality;

/// <summary>One metric row inside an insight report.</summary>
public sealed record InsightMetric(string Label, string Value);

/// <summary>
/// Uniform output of every analyzer: a name, a status, an optional 0–1 score,
/// metric rows and human-readable findings. The dashboard renders these
/// generically, so adding a new algorithm is one class + one registration —
/// no UI work.
/// </summary>
public sealed record InsightReport(
    string Name,
    string Algorithm,
    string Status,          // "ok" | "no-data"
    double? Score,          // 0–1 when the analyzer produces a headline number
    List<InsightMetric> Metrics,
    List<string> Findings);

public interface IInsightAnalyzer
{
    InsightReport Analyze(CopilotSession session);
}

/// <summary>Runs every registered analyzer against a session.</summary>
public sealed class InsightPipeline(IEnumerable<IInsightAnalyzer> analyzers)
{
    private readonly List<IInsightAnalyzer> _analyzers = analyzers.ToList();

    public List<InsightReport> Analyze(CopilotSession session)
    {
        var reports = new List<InsightReport>(_analyzers.Count);
        foreach (var analyzer in _analyzers)
        {
            try { reports.Add(analyzer.Analyze(session)); }
            catch (Exception ex)
            {
                reports.Add(new InsightReport(analyzer.GetType().Name, "-", "no-data", null, [],
                    [$"Analyzer failed: {ex.Message}"]));
            }
        }
        return reports;
    }
}
