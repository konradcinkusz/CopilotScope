using CopilotScope.Collector.Domain;

namespace CopilotScope.Collector.Quality;

public sealed record TurnReport(
    int Index, DateTimeOffset Start, double DurationMs,
    int ChatCalls, int ChatErrors, int ToolCalls, int ToolErrors,
    long InputTokens, long OutputTokens, double AvgTtftMs,
    double Score, List<string> Reasons,
    string? Model = null);

public sealed record TurnAnalysis(
    string Algorithm,
    List<TurnReport> Turns,
    int? BestIndex, int? WorstIndex,
    List<string> Findings);

/// <summary>
/// Turn-level Friction &amp; Repair Analysis (TFRA).
///
/// One "turn" is one invoke_agent trace. Each turn starts at 1.0 and collects
/// telemetry-observable friction penalties: LLM/tool errors, latency degradation
/// relative to the session's own median TTFT (self-referential, so slow models
/// aren't punished for being slow — only turns that are slow *for this session*),
/// and repair loops (tool-call bursts with errors, the trace signature of the
/// agent retrying its way out of a failure). The best/worst turns come with
/// human-readable reasons, so the answer to "which part of this chat was good
/// and why" is explainable rather than a bare number.
///
/// Chosen for implementation because it runs purely on metadata (no prompt
/// content, no judge model, no network) and is interpretable by construction.
/// </summary>
public static class SegmentAnalyzer
{
    public static TurnAnalysis Analyze(CopilotSession session) => session.Snapshot(s =>
    {
        var turns = s.TurnList.Where(t => t.ChatCalls + t.ToolCalls > 0).ToList();
        if (turns.Count == 0)
            return new TurnAnalysis("TFRA (turn-level friction & repair)", [], null, null,
                ["No completed turns yet."]);

        var medianTtft = Median(turns.Where(t => t.TtftCount > 0).Select(t => t.AvgTtftMs).ToList());
        var medianToolRatio = Median(turns.Select(t => t.ToolCalls / Math.Max(1.0, t.ChatCalls)).ToList());

        var reports = new List<TurnReport>();
        foreach (var t in turns)
        {
            var score = 1.0;
            var reasons = new List<string>();

            if (t.ChatErrors > 0)
            {
                score -= 0.35 * Math.Min(t.ChatErrors, 2);
                reasons.Add($"{t.ChatErrors} LLM error(s)");
            }
            if (t.ToolErrors > 0)
            {
                score -= 0.15 * Math.Min(t.ToolErrors, 3);
                reasons.Add($"{t.ToolErrors} tool error(s)");
            }

            if (medianTtft > 0 && t.TtftCount > 0)
            {
                var ratio = t.AvgTtftMs / medianTtft;
                if (ratio >= 3.0) { score -= 0.30; reasons.Add($"TTFT {ratio:0.0}× session median"); }
                else if (ratio >= 1.5) { score -= 0.15; reasons.Add($"TTFT {ratio:0.0}× session median"); }
                else if (ratio <= 0.75) reasons.Add("fast response (below median TTFT)");
            }

            // Repair-loop signature: unusually many tool calls per chat call AND failures —
            // the agent is retrying, not progressing.
            var toolRatio = t.ToolCalls / Math.Max(1.0, t.ChatCalls);
            if (t.ToolErrors > 0 && medianToolRatio > 0 && toolRatio >= 2 * medianToolRatio && t.ToolCalls >= 3)
            {
                score -= 0.10;
                reasons.Add($"repair loop ({t.ToolCalls} tool calls for {t.ChatCalls} chat call(s))");
            }

            if (reasons.Count == 0) reasons.Add("clean turn — no errors, typical latency");

            reports.Add(new TurnReport(
                t.Index, t.Start, t.DurationMs,
                t.ChatCalls, t.ChatErrors, t.ToolCalls, t.ToolErrors,
                t.InputTokens, t.OutputTokens, t.AvgTtftMs,
                Math.Clamp(score, 0, 1), reasons, t.PrimaryModel));
        }

        var best = reports.OrderByDescending(r => r.Score).ThenBy(r => r.AvgTtftMs).First();
        var worst = reports.OrderBy(r => r.Score).ThenByDescending(r => r.AvgTtftMs).First();

        var allClean = reports.All(r => r.Score >= 1.0);
        var findings = new List<string>();

        if (allClean)
        {
            findings.Add($"All {reports.Count} turn{(reports.Count == 1 ? "" : "s")} clean — no errors or latency outliers detected.");
            // No best/worst when everything is uniform
            return new TurnAnalysis("TFRA (turn-level friction & repair)", reports, null, null, findings);
        }

        // Highlight best/worst only when they differ meaningfully
        var scoreDelta = best.Score - worst.Score;
        if (scoreDelta > 0.05 || best.Index != worst.Index)
        {
            findings.Add($"Best: turn {best.Index + 1} — {string.Join(", ", best.Reasons)}");
            findings.Add($"Worst: turn {worst.Index + 1} — {string.Join(", ", worst.Reasons)}");
        }

        var cleanCount = reports.Count(r => r.Score >= 1.0);
        if (cleanCount > 0 && cleanCount < reports.Count)
            findings.Add($"{cleanCount} of {reports.Count} turn{(reports.Count == 1 ? "" : "s")} had no issues.");

        var totalErrors = reports.Sum(r => r.ChatErrors + r.ToolErrors);
        var worstErrors = worst.ChatErrors + worst.ToolErrors;
        if (totalErrors > 0 && worstErrors * 2 >= totalErrors && reports.Count > 1)
            findings.Add($"Errors are concentrated in turn {worst.Index + 1} ({worstErrors}/{totalErrors}) — the rest of the session is healthy.");

        return new TurnAnalysis("TFRA (turn-level friction & repair)", reports,
            best.Index, worst.Index, findings);
    });

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.Order().ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
